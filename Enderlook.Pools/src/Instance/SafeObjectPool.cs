using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Enderlook.Pools;

/// <summary>
/// A fast, dynamically-sized and thread-safe object pool to store objects.<br/>
/// This pool can be configured to automatically call <see cref="IDisposable"/> of elements that are free (for example during trimming, when pool is full or when the pool is disposed itself).
/// </summary>
/// <typeparam name="T">Type of object to pool</typeparam>
public sealed class SafeObjectPool<T> : ObjectPool<T>
    where T : class
{
    /// <summary>
    /// Delegate that instantiates new object.
    /// </summary>
    private readonly Func<T> factory;

    /// <summary>
    /// Storage for the pool objects.<br/>
    /// The array is not an stack so the whole array must be traversed to find objects.
    /// </summary>
    private readonly ObjectWrapper[] array;

    /// <summary>
    /// The first item is stored in a dedicated field because we expect to be able to satisfy most requests from it.
    /// </summary>
    private T? firstElement;

    /// <summary>
    /// A dynamic-size stack reserve of objects.<br/>
    /// When <see cref="array"/> get fulls, the first half of it is emptied and its element are moved here.<br/>
    /// When <see cref="array"/> gets empty, the first half of it is fulled with elements from this reserve.<br/>
    /// Those operations are done in a batch to reduce the amount of times this requires to be acceded.<br/>
    /// However, those operations only moves the first half of the array to prevent a point where this is executed on each rent or return.
    /// </summary>
    private ObjectWrapper[]? reserve;

    /// <summary>
    /// Keep tracks of the amount of used slots in <see cref="reserve"/>.
    /// </summary>
    private int reserveCount;

    /// <summary>
    /// Keep record of last time <see cref="reserve"/> was trimmed;
    /// </summary>
    private int reserveMillisecondsTimeStamp;

    /// <summary>
    /// Keep record of last time <see cref="array"/> was trimmed;
    /// </summary>
    private int arrayMillisecondsTimeStamp;

    /// <summary>
    /// Capacity of the pool.<br/>
    /// This region of the pool support concurrent access.<br/>
    /// The capacity should preferably be not greater than <c><see cref="Environment.ProcessorCount"/> * 2</c>, since it's fully iterated before accessing the reserve.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Throw when <see langword="value"/> is lower than 1.</exception>
    public int Capacity
    {
        get => array.Length + 1;
        init
        {
            if (value < 1) Utils.ThrowArgumentOutOfRangeException_CapacityCanNotBeLowerThanOne();
            array = new ObjectWrapper[value - 1]; // -1 due to firstElement.
        }
    }

    /// <summary>
    /// Current capacity of the reserve.<br/>
    /// This reserve pool is only acceded when the non-reserve capacity gets full or empty.<br/>
    /// This is because this region can only be acceded by a single thread<br/>
    /// This pool has a dynamic size so this value represent the initial size of the pool which may enlarge or shrink over time.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Throw when <see langword="value"/> is negative.</exception>
    public int Reserve
    {
        get
        {
            ObjectWrapper[]? reserve_ = Utils.NullExchange(ref reserve);
            int count = reserve_.Length;
            reserve = reserve_;
            return count;
        }
        init
        {
            if (value < 0) Utils.ThrowArgumentOutOfRangeException_ReserveCanNotBeNegative();
            reserve = new ObjectWrapper[value];
        }
    }

    /// <summary>
    /// Determines if the reserve pool is allowed to grow and shrink given its usage.
    /// </summary>
    public bool IsReserveDynamic { get; init; } = true;

    /// <summary>
    /// If this value is not <see langword="null"/>, the callback will be executed on each element which is free from the pool.<br/>
    /// That is, it will be called in elements not being stored during <see cref="Return(T)"/>, or during elements free by <see cref="Trim(bool)"/> or its automatic cleaning.<br/>
    /// If no value is specified, by default it will include a callback which executes <see cref="IDisposable.Dispose"/> on elements which can be casted to <see cref="IDisposable"/>.
    /// </summary>
    /// <remarks>If no value is specified, by default it will include a callback, but we actually don't call it.<br/>
    /// Instead we run the behaviour inline. This is to avoid the delegate call.</remarks>
    public Action<T>? FreeCallback
    {
        get => disposeMode switch
        {
            Disposing<T>.HAS_CUSTOM_DISPOSING => freeCallback,
            Disposing<T>.NULL_CUSTOM_DISPOSING => null,
            _ => Disposing<T>.DisposeAction,
        };
        init
        {
            if (value is null)
            {
                disposeMode = Disposing<T>.NULL_CUSTOM_DISPOSING;
#pragma warning disable CA1816 // Dispose methods should call SuppressFinalize
                GC.SuppressFinalize(this);
#pragma warning restore CA1816 // Dispose methods should call SuppressFinalize
            }
            else if (ReferenceEquals(value, Disposing<T>.DisposeAction))
            {
                freeCallback = null;
                disposeMode = Disposing<T>.DisposableMode;
            }
            else
            {
                freeCallback = value;
                disposeMode = Disposing<T>.HAS_CUSTOM_DISPOSING;
            }
        }
    }
    private Action<T>? freeCallback;
    private byte disposeMode = Disposing<T>.DisposableMode;

    /// <summary>
    /// Delegate which instantiates new objects.<br/>
    /// If no delegate was provided during construction of the pool, a default one which calls the parameterless constructor (or <see langword="default"/> for value types if missing) will be provided.
    /// </summary>
    public Func<T> Factory => factory;

    /// <summary>
    /// Creates a pool of objects.
    /// </summary>
    /// <param name="factory">Delegate used to construct instances of the pooled objects.<br/>
    /// If no delegate is provided, a factory with the parameterless constructor (or <see langword="default"/> for value types if missing) of <typeparamref name="T"/> will be used.</param>
    public SafeObjectPool(Func<T>? factory)
    {
        this.factory = factory ?? ObjectPoolHelper<T>.Factory;
        int capacity = Environment.ProcessorCount * 2;
        array = new ObjectWrapper[capacity - 1]; // -1 due to firstElement.
        reserve = new ObjectWrapper[capacity];
        GCCallback<T> _ = new(this);
    }

    /// <summary>
    /// Creates a pool of objects.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SafeObjectPool() : this(null) { }

    /// <summary>
    /// Calls the <see cref="IDisposable.Dispose"/> of all the pooled elements.
    /// </summary>
    ~SafeObjectPool()
    {
        if (disposeMode is Disposing<T>.NULL_CUSTOM_DISPOSING or Disposing<T>.CAN_NOT_IMPLEMENT_IDISPOSABLE)
            return;
        Trim(true);
    }

    /// <inheritdoc cref="ObjectPool{T}.ApproximateCount"/>
    public override int ApproximateCount()
    {
        int count = firstElement is null ? 0 : 1;
        ObjectWrapper[] items = array;
        ref ObjectWrapper current = ref Utils.GetArrayDataReference(items);
        ref ObjectWrapper end = ref Unsafe.Add(ref current, items.Length);
        while (Unsafe.IsAddressLessThan(ref current, ref end))
        {
            if (current.Value is not null)
                count++;
            current = ref Unsafe.Add(ref current, 1);
        }
        return count + reserveCount;
    }

    /// <inheritdoc cref="ObjectPool{T}.Rent"/>
    public override T Rent()
    {
        // First, we examine the first element.
        // If that fails, we look at the remaining elements.
        // Note that intitial read are optimistically not synchronized. This is intentional.
        // We will interlock only when we have a candidate.
        // In a worst case we may miss some recently returned objects.
        T? element = firstElement;
        if (element is null || element != Interlocked.CompareExchange(ref firstElement, null, element))
        {
            // Next, we look at all remaining elements.
            ObjectWrapper[] items = array;
            ref ObjectWrapper current = ref Utils.GetArrayDataReference(items);
            ref ObjectWrapper end = ref Unsafe.Add(ref current, items.Length);
            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                // Note that intitial read are optimistically not synchronized. This is intentional.
                // We will interlock only when we have a candidate.
                // In a worst case we may miss some recently returned objects.
                Debug.Assert(current.Value is null or T);
                element = Unsafe.As<T?>(current.Value);
                if (element is not null && element == Interlocked.CompareExchange(ref current.Value, null, element))
                    break;
                current = ref Unsafe.Add(ref current, 1);
            }

            // Next, we look at the reserve if it has elements.
            element = reserveCount > 0 ? FillFromReserve() : factory();
        }

        return element;
    }

    /// <summary>
    /// Return rented object to pool.
    /// </summary>
    /// <param name="element">Object to return.</param>
    public override void Return(T element)
    {
        // First, we examine the first element.
        // Then do interlocking.
        if (firstElement is not null || Interlocked.CompareExchange(ref firstElement, element, null) is not null)
        {
            ObjectWrapper[] items = array;
            ref ObjectWrapper current = ref Utils.GetArrayDataReference(items);
            ref ObjectWrapper end = ref Unsafe.Add(ref current, items.Length);
            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                // Intentionally we first check if there is an element to avoid unecessary locking.
                if (current.Value is null && Interlocked.CompareExchange(ref current.Value, element, null) is null)
                    return;
                current = ref Unsafe.Add(ref current, 1);
            }

            SendToReserve(element);
        }
    }

    /// <inheritdoc cref="ObjectPool{T}.Trim(bool)"/>
    public override void Trim(bool force = false)
    {
        const int ArrayLowAfterMilliseconds = 60 * 1000; // Trim after 60 seconds for low pressure.
        const int ArrayMediumAfterMilliseconds = 60 * 1000; // Trim after 60 seconds for medium pressure.
        const int ArrayHighTrimAfterMilliseconds = 10 * 1000; // Trim after 10 seconds for high pressure.
        const int ArrayLowTrimCount = 1; // Trim one item when pressure is low.
        const int ArrayMediumTrimCount = 2; // Trim two items when pressure is moderate.

        const int ReserveLowTrimAfterMilliseconds = 90 * 1000; // Trim after 90 seconds for low pressure.
        const int ReserveMediumTrimAfterMilliseconds = 45 * 1000; // Trim after 45 seconds for low pressure.
        const float ReserveLowTrimPercentage = .10f; // Trim 10% of objects for low pressure;
        const float ReserveMediumTrimPercentage = .30f; // Trim 30% of objects for moderate pressure.

        const int ReserveShrinkFactorToStart = 4; // Reserve must be using a quarter of its capacity to shrink.
        const int ReserveShrinkFactor = 2; // Shrink reserve by half of its length.

        int currentMilliseconds = Environment.TickCount;

        firstElement = null; // We always trim the first element.

        ObjectWrapper[] items = array;
        int length = items.Length;

        int arrayTrimMilliseconds;
        int arrayTrimCount;
        int reserveTrimMilliseconds;
        float reserveTrimPercentage;
        if (force)
        {
            // Forces to clear everything regardless of time.
            arrayTrimCount = length;
            arrayTrimMilliseconds = 0;
            reserveTrimMilliseconds = 0;
            reserveTrimPercentage = 1;
        }
        else
        {
            switch (Utils.GetMemoryPressure())
            {
                case Utils.MemoryPressure.High:
                    arrayTrimCount = length;
                    arrayTrimMilliseconds = ArrayHighTrimAfterMilliseconds;
                    // Forces to clear everything regardless of time.
                    reserveTrimMilliseconds = 0;
                    reserveTrimPercentage = 1;
                    break;
                case Utils.MemoryPressure.Medium:
                    arrayTrimCount = ArrayMediumTrimCount;
                    arrayTrimMilliseconds = ArrayMediumAfterMilliseconds;
                    reserveTrimMilliseconds = ReserveMediumTrimAfterMilliseconds;
                    reserveTrimPercentage = ReserveMediumTrimPercentage;
                    break;
                default:
                    arrayTrimCount = ArrayLowTrimCount;
                    arrayTrimMilliseconds = ArrayLowAfterMilliseconds;
                    reserveTrimMilliseconds = ReserveLowTrimAfterMilliseconds;
                    reserveTrimPercentage = ReserveLowTrimPercentage;
                    break;
            }
        }

        int arrayMillisecondsTimeStamp_ = arrayMillisecondsTimeStamp;
        if (arrayMillisecondsTimeStamp_ == 0)
            arrayMillisecondsTimeStamp_ = arrayMillisecondsTimeStamp = currentMilliseconds;

        if ((currentMilliseconds - arrayMillisecondsTimeStamp_) > arrayTrimMilliseconds)
        {
            // We've elapsed enough time since the last clean.
            // Drop the top items so they can be collected and make the pool look a little newer.

            bool complete = disposeMode switch
            {
                Disposing<T>.IMPLEMENT_IDISPOSABLE => FreeHelper.ClearPool(new FreeHelper.CallDisposeReference(), items, arrayTrimCount),
                Disposing<T>.MAY_IMPLEMENT_IDISPOSABLE => FreeHelper.ClearPool(new FreeHelper.TryCallDisposeReference(), items, arrayTrimCount),
                Disposing<T>.HAS_CUSTOM_DISPOSING => FreeHelper.ClearPool(new FreeHelper.CustomFreeReference<T>(freeCallback!), items, arrayTrimCount),
                _ => FreeHelper.ClearPool(items, arrayTrimCount),
            };

            if (complete)
                arrayMillisecondsTimeStamp = arrayMillisecondsTimeStamp_ + arrayMillisecondsTimeStamp_ / 4; // Give the remaining items a bit more time.
            else
                arrayMillisecondsTimeStamp = 0;
        }

        int oldReserveCount = reserveCount;
        int newReserveCount;
        int newReserveLength;
        int itemsLength = items.Length;
        ObjectWrapper[] reserve_ = Utils.NullExchange(ref reserve);
        int oldReserveLength = reserve_.Length;
        bool isReserveDynamic = IsReserveDynamic;
        // Under high pressure, we don't wait time to trim, so we remove all objects in reserve.
        if (reserveTrimPercentage == 1)
        {
            Debug.Assert(reserveTrimMilliseconds == 0);
            newReserveCount = 0;
            newReserveLength = itemsLength;
            reserveMillisecondsTimeStamp = 0;
        }
        else
        {
            int reserveMillisecondsTimeStamp_ = reserveMillisecondsTimeStamp;
            if (reserveMillisecondsTimeStamp_ == 0)
                reserveMillisecondsTimeStamp_ = reserveMillisecondsTimeStamp = currentMilliseconds;

            if ((currentMilliseconds - reserveMillisecondsTimeStamp_) > reserveTrimMilliseconds)
            {
                // Otherwise, remove a percentage of all stored objects in the reserve, based on how long was the last trimming.
                // This time is approximate, with the time set not when the element is stored but when we see it during a Trim,
                // so it takes at least two Trim calls (and thus two gen2 GCs) to drop elements, unless we're in high memory pressure.

                int toRemove = (int)Math.Ceiling(oldReserveCount * reserveTrimPercentage);
                newReserveCount = Math.Max(oldReserveCount - toRemove, 0);

                // Since the global reserve has a dynamic size, we shrink the reserve if it gets too small.
                if (isReserveDynamic && oldReserveLength / newReserveCount >= ReserveShrinkFactorToStart)
                {
                    Debug.Assert(ReserveShrinkFactorToStart >= ReserveShrinkFactor);
                    newReserveLength = Math.Min(oldReserveLength / ReserveShrinkFactor, itemsLength);
                    newReserveLength = Math.Max(newReserveLength, newReserveCount);
                }
                else
                    newReserveLength = oldReserveCount;
            }
            else
            {
                newReserveCount = oldReserveCount;
                newReserveLength = oldReserveLength;
            }
        }

        newReserveLength = isReserveDynamic ? Math.Max(newReserveLength, Math.Min(oldReserveLength, itemsLength)) : oldReserveLength;

        if (oldReserveCount > 0)
        {
            switch (disposeMode)
            {
                case Disposing<T>.IMPLEMENT_IDISPOSABLE:
                    FreeHelper.ClearReserve(new FreeHelper.CallDisposeReference(), reserve_, oldReserveCount, newReserveCount);
                    break;
                case Disposing<T>.MAY_IMPLEMENT_IDISPOSABLE:
                    FreeHelper.ClearReserve(new FreeHelper.TryCallDisposeReference(), reserve_, oldReserveCount, newReserveCount);
                    break;
                case Disposing<T>.HAS_CUSTOM_DISPOSING:
                    FreeHelper.ClearReserve(new FreeHelper.CustomFreeReference<T>(freeCallback!), reserve_, oldReserveCount, newReserveCount);
                    break;
            }
            // Clear the array only if we are not gonna replace it with a new one.
            if (newReserveLength != oldReserveLength)
                Array.Clear(reserve_, newReserveCount, oldReserveCount - newReserveCount);
        }

        if (newReserveLength != oldReserveLength)
        {
            ObjectWrapper[] newReserve = new ObjectWrapper[newReserveLength];
            Array.Copy(reserve_, newReserve, newReserveCount);
            reserve_ = newReserve;
        }

        reserveCount = newReserveCount;
        reserve = reserve_;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T FillFromReserve()
    {
        ObjectWrapper[] items = array;
        ObjectWrapper[] reserve_ = Utils.NullExchange(ref reserve);

        int oldCount = reserveCount;
#if DEBUG
        int count = oldCount;
#endif
        if (oldCount > 0)
        {
#if DEBUG
            Debug.Assert(--count < reserve_.Length);
#endif
            ref ObjectWrapper startReserve = ref Utils.GetArrayDataReference(reserve_);
            ref ObjectWrapper currentReserve = ref Unsafe.Add(ref startReserve, oldCount - 1);
            Debug.Assert(currentReserve.Value is T);
            T element = Unsafe.As<T>(currentReserve.Value);
            Debug.Assert(element is not null);

            ref ObjectWrapper current = ref Utils.GetArrayDataReference(items);
            ref ObjectWrapper end = ref Unsafe.Add(ref current, items.Length / 2);
            while (Unsafe.IsAddressLessThan(ref current, ref end) && Unsafe.IsAddressGreaterThan(ref currentReserve, ref startReserve))
            {
#if DEBUG
                Debug.Assert(count > 0);
#endif
                // Note that intitial read and write are optimistically not synchronized. This is intentional.
                // In a worst case we may miss some recently returned objects or accidentally free objects.
                if (current.Value is null)
                {
#if DEBUG
                    Debug.Assert(--count < reserve_.Length);
#endif
                    ref ObjectWrapper newReserve = ref Unsafe.Subtract(ref currentReserve, 1);
                    if (Interlocked.CompareExchange(ref current.Value, newReserve.Value, null) is null)
                        currentReserve = ref newReserve;
                }
                current = ref Unsafe.Add(ref current, 1);
            }

            int count_ = (int)Unsafe.ByteOffset(ref startReserve, ref currentReserve) / Unsafe.SizeOf<ObjectWrapper>();
#if DEBUG
            Debug.Assert(count_ == count);
#endif
            Array.Clear(reserve_, count_, oldCount - count_);

            reserveCount = count_;
            reserve = reserve_;
            return element;
        }
        else
        {
            reserve = reserve_;
            return factory();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SendToReserve(T obj)
    {
        if (obj is null) return;

        ObjectWrapper[] items = array;
        ObjectWrapper[] reserve_ = Utils.NullExchange(ref reserve);

        int reserveCount_ = reserveCount;
#if DEBUG
        int count = reserveCount_;
#endif
        if (reserveCount_ + 1 + (items.Length / 2) > reserve_.Length)
        {
            if (IsReserveDynamic)
                Array.Resize(ref reserve_, Math.Max(reserve_.Length * 2, 1));
            else if (reserveCount_ + 1 == reserve_.Length)
            {
                switch (disposeMode)
                {
                    case Disposing<T>.IMPLEMENT_IDISPOSABLE:
                        Unsafe.As<IDisposable>(obj).Dispose();
                        break;
                    case Disposing<T>.MAY_IMPLEMENT_IDISPOSABLE when obj is IDisposable disposable:
                        disposable.Dispose();
                        break;
                    case Disposing<T>.HAS_CUSTOM_DISPOSING:
                        Action<T>? freeCallback_ = freeCallback;
                        Debug.Assert(freeCallback_ is not null);
                        freeCallback_(Unsafe.As<T>(obj));
                        break;
                }
                reserve = reserve_;
                return;
            }
        }

        ref ObjectWrapper startReserve = ref Utils.GetArrayDataReference(reserve_);
        ref ObjectWrapper endReserve = ref Unsafe.Add(ref startReserve, reserve_.Length);

#if DEBUG
        Debug.Assert(count++ < reserve_.Length);
#endif
        Debug.Assert(reserveCount_ < reserve_.Length);
        ref ObjectWrapper currentReserve = ref Unsafe.Add(ref startReserve, reserveCount_);
        currentReserve.Value = obj;
        currentReserve = ref Unsafe.Add(ref currentReserve, 1);
        if (!Unsafe.IsAddressLessThan(ref currentReserve, ref endReserve))
        {
            ref ObjectWrapper current = ref Utils.GetArrayDataReference(items);
            ref ObjectWrapper end = ref Unsafe.Add(ref current, items.Length / 2);

            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                // We don't use an optimistically not synchronized initial read in this part.
                // This is because we expect the majority of the array to be filled.
                // So it's not worth doing an initial read to check that.
                object? element_ = Interlocked.Exchange(ref current.Value, null);
                Debug.Assert(element_ is null or T);
                T? element = Unsafe.As<T?>(element_);
                if (element is not null)
                {
#if DEBUG
                    Debug.Assert(count++ < reserve_.Length);
#endif
                    currentReserve = ref Unsafe.Add(ref currentReserve, 1);
                    currentReserve.Value = element;
                    if (Unsafe.IsAddressLessThan(ref currentReserve, ref endReserve))
                        break;
                }
                current = ref Unsafe.Add(ref current, 1);
            }
        }

        int count_ = (int)Unsafe.ByteOffset(ref startReserve, ref currentReserve) / Unsafe.SizeOf<ObjectWrapper>();
#if DEBUG
        Debug.Assert(count_ == count);
#endif

        reserveCount = count_;
        reserve = reserve_;
    }
}