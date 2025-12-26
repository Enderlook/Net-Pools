using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Enderlook.Pools;

/// <summary>
/// A fast, dynamically-sized and thread-safe array pool to store arrays of an specific length.<br/>
/// </summary>
/// <typeparam name="T">Type of element array to pool</typeparam>
public sealed class SafeExactLengthArrayObjectPool<T> : ArrayObjectPool<T>
{
    /// <summary>
    /// Determines the length of the pooled arrays.
    /// </summary>
    public int Length { get; }

    /// <summary>
    /// Storage for the pool objects.<br/>
    /// The array is not an stack so the whole array must be traversed to find objects.
    /// </summary>
    internal readonly Array? array;

    /// <summary>
    /// The first item is stored in a dedicated field because we expect to be able to satisfy most requests from it.
    /// </summary>
    private T[]? firstElement;

    /// <summary>
    /// A dynamic-size stack reserve of objects.<br/>
    /// When <see cref="array"/> get fulls, the first half of it is emptied and its element are moved here.<br/>
    /// When <see cref="array"/> gets empty, the first half of it is fulled with elements from this reserve.<br/>
    /// Those operations are done in a batch to reduce the amount of times this requires to be acceded.<br/>
    /// However, those operations only moves the first half of the array to prevent a point where this is executed on each rent or return.
    /// </summary>
    internal Array? reserve;

    /// <summary>
    /// Keep tracks of the amount of used slots in <see cref="reserve"/>.
    /// </summary>
    internal int reserveCount;

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
        get
        {
            if (pool is SafeExactLengthArrayObjectPool<T> p)
                return p.Capacity;

            Debug.Assert(array is ObjectWrapper[]);
            ObjectWrapper[] items = Unsafe.As<ObjectWrapper[]>(array);
            return items.Length + 1;
        }
        init
        {
            if (value < 1) Utils.ThrowArgumentOutOfRangeException_CapacityCanNotBeLowerThanOne();
            value -= 1;  // -1 due to firstElement.
            array = new ObjectWrapper[value];
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
            if (pool is SafeExactLengthArrayObjectPool<T> p)
                return p.Reserve;

            Array? reserve_ = Utils.NullExchange(ref reserve);
            int count;
            Debug.Assert(reserve_ is ObjectWrapper[]);
            count = Unsafe.As<ObjectWrapper[]>(reserve_).Length;
            reserve = reserve_;
            return count;
        }
        init
        {
            if (value < 0) Utils.ThrowArgumentOutOfRangeException_ReserveCanNotBeNegative();
            reserve = typeof(T).IsValueType ? new T[value] : new ObjectWrapper[value];
        }
    }

    /// <summary>
    /// Determines if the reserve pool is not allowed to grow nor shrink given its usage.
    /// </summary>
    public bool IsReserveFixed { get; init; }

    /// <summary>
    /// Determines the default array clearing strategy.<br/>
    /// If this is <see langword="true"/>, buffers that will be stored to enable subsequent reuse in <see cref="Return(T[])"/>, will have their content cleared so that a subsequent consumer will not see the previous consumer's content.<br/>
    /// If <see langword="false"/> or if the pool will release the buffer, the array's contents are left unchanged.
    /// </summary>
    public override bool ShouldClearArrayOnReturnByDefault { get; }

    private readonly SafeExactLengthArrayObjectPool<T>? pool;

    /// <summary>
    /// Creates a pool of exact length array.
    /// </summary>
    /// <param name="length">Length of the pooled arrays.</param>
    /// <param name="shouldClearArrayOnReturnByDefault">If this is <see langword="true"/>, buffers that will be stored to enable subsequent reuse in <see cref="Return(T[])"/>, will have their content cleared so that a subsequent consumer will not see the previous consumer's content.<br/>
    /// If <see langword="false"/> or if the pool will release the buffer, the array's contents are left unchanged.</param>
    public SafeExactLengthArrayObjectPool(int length, bool shouldClearArrayOnReturnByDefault = false)
    {
        Length = length;
        ShouldClearArrayOnReturnByDefault = shouldClearArrayOnReturnByDefault;
        int capacity = Environment.ProcessorCount * 2;
        reserve = new ObjectWrapper[capacity];
        array = new ObjectWrapper[capacity - 1]; // -1 due to firstElement.
        GCCallbackObject<T[]> _ = new(this);
    }

    internal SafeExactLengthArrayObjectPool(int length, SafeExactLengthArrayObjectPool<T>? pool, bool shouldClearArrayOnReturnByDefault)
    {
        this.pool = pool;
        Length = length;
        ShouldClearArrayOnReturnByDefault = shouldClearArrayOnReturnByDefault;
        if (pool is null)
        {
            int capacity = Environment.ProcessorCount * 2;
            reserve = new ObjectWrapper[capacity];
            array = new ObjectWrapper[capacity - 1]; // -1 due to firstElement.
        }
    }

    /// <inheritdoc cref="ObjectPool{T}.ApproximateCount"/>
    public override int ApproximateCount()
    {
        if (pool is SafeExactLengthArrayObjectPool<T> p)
            return p.ApproximateCount();

        int count = firstElement is null ? 0 : 1;
        Debug.Assert(array is ObjectWrapper[]);
        ObjectWrapper[] items = Unsafe.As<ObjectWrapper[]>(array);
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
    public override T[] Rent()
    {
        if (pool is SafeExactLengthArrayObjectPool<T> p)
            return p.Rent();

        // First, we examine the first element.
        // If that fails, we look at the remaining elements.
        // Note that intitial read are optimistically not synchronized. This is intentional.
        // We will interlock only when we have a candidate.
        // In a worst case we may miss some recently returned objects.
        T[]? element = firstElement;
        if (element is null || !ReferenceEquals(element, Interlocked.CompareExchange(ref firstElement, null, element)))
        {
            // Next, we look at all remaining elements.
            Debug.Assert(array is ObjectWrapper[]);
            ObjectWrapper[] items = Unsafe.As<ObjectWrapper[]>(array);
            ref ObjectWrapper current = ref Utils.GetArrayDataReference(items);
            ref ObjectWrapper end = ref Unsafe.Add(ref current, items.Length);
            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                // Note that intitial read are optimistically not synchronized. This is intentional.
                // We will interlock only when we have a candidate.
                // In a worst case we may miss some recently returned objects.
                Debug.Assert(current.Value is null or T[]);
                element = Unsafe.As<object?, T[]?>(ref current.Value);
                if (element is not null && ReferenceEquals(element, Interlocked.CompareExchange(ref current.Value, null, element)))
                    return element;
                current = ref Unsafe.Add(ref current, 1);
            }

            // Next, we look at the reserve if it has elements.
            int length = Length;
            if (length == 0)
                return Array.Empty<T>();

#if NET5_0_OR_GREATER
            element = reserveCount > 0 ? this.FillFromReserveReference() : GC.AllocateUninitializedArray<T>(Length);
#else
            element = reserveCount > 0 ? this.FillFromReserveReference() : new T[length];
#endif
        }

        return element;
    }

    /// <summary>
    /// Return rented object to pool.
    /// </summary>
    /// <param name="element">Object to return.</param>
    /// <remarks>It uses a clearing policy specified by <see cref="ShouldClearArrayOnReturnByDefault"/>.</remarks>
    public override void Return(T[] element)
    {
        if (element is null) return;
        if (element.Length != Length) Utils.ThrowArgumentOutOfRangeException_ArrayLength();

        if (pool is SafeExactLengthArrayObjectPool<T> p)
            p.Return_(element, ShouldClearArrayOnReturnByDefault);
        else
            Return_(element, ShouldClearArrayOnReturnByDefault);
    }

    /// <summary>
    /// Return rented object to pool.
    /// </summary>
    /// <param name="element">Object to return.</param>
    /// <param name="clearArrayOnReturn">If this is <see langword="true"/>, buffers that will be stored to enable subsequent reuse in <see cref="ObjectPool{T}.Return(T)"/>, will have their content cleared so that a subsequent consumer will not see the previous consumer's content.<br/>
    /// If <see langword="false"/> or if the pool will release the buffer, the array's contents are left unchanged.</param>
    public override void Return(T[] element, bool clearArrayOnReturn)
    {
        if (element is null) return;
        if (element.Length != Length) Utils.ThrowArgumentOutOfRangeException_ArrayLength();

        Return_(element, clearArrayOnReturn);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Return_(T[] element, bool clearArray)
    {
        Debug.Assert(element.Length == Length);

        if (Length == 0)
            return;

        if (clearArray)
        {
#if NET6_0_OR_GREATER
            Array.Clear(element);
#else
            Array.Clear(element, 0, element.Length);
#endif
        }

        // First, we examine the first element.
        // Then do interlocking.

        if (firstElement is not null || Interlocked.CompareExchange(ref firstElement, element, null) is not null)
        {
            Debug.Assert(array is ObjectWrapper[]);
            ObjectWrapper[] items = Unsafe.As<ObjectWrapper[]>(array);
            ref ObjectWrapper current = ref Utils.GetArrayDataReference(items);
            ref ObjectWrapper end = ref Unsafe.Add(ref current, items.Length);
            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                // Intentionally we first check if there is an element to avoid unecessary locking.
                if (current.Value is null && Interlocked.CompareExchange(ref current.Value, element, null) is null)
                    return;
                current = ref Unsafe.Add(ref current, 1);
            }

            this.SendToReserve(element);
        }
    }

    /// <inheritdoc cref="ObjectPool{T}.Trim(bool)"/>
    public override void Trim(bool force = false)
    {
        if (pool is SafeExactLengthArrayObjectPool<T> p)
        {
            p.Trim(force);
            return;
        }

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

        Debug.Assert(array is ObjectWrapper[]);
        ObjectWrapper[] items = Unsafe.As<ObjectWrapper[]>(array);
        int itemsLength = items.Length;

        int arrayTrimMilliseconds;
        int arrayTrimCount;
        int reserveTrimMilliseconds;
        float reserveTrimPercentage;
        if (force)
        {
            // Forces to clear everything regardless of time.
            arrayTrimCount = itemsLength;
            arrayTrimMilliseconds = 0;
            reserveTrimMilliseconds = 0;
            reserveTrimPercentage = 1;
        }
        else
        {
            switch (Utils.GetMemoryPressure())
            {
                case Utils.MemoryPressure.High:
                    // Forces to clear everything regardless of time.
                    arrayTrimCount = itemsLength;
                    arrayTrimMilliseconds = ArrayHighTrimAfterMilliseconds;
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

            bool complete = ObjectPoolHelper.ClearPool(items, arrayTrimCount);

            if (complete)
                arrayMillisecondsTimeStamp = arrayMillisecondsTimeStamp_ + arrayMillisecondsTimeStamp_ / 4; // Give the remaining items a bit more time.
            else
                arrayMillisecondsTimeStamp = 0;
        }

        int newReserveCount;
        int newReserveLength;
        Debug.Assert(array is ObjectWrapper[]);
        Array reserve_ = Utils.NullExchange(ref reserve);
        int oldReserveCount = reserveCount;
        int oldReserveLength = reserve_.Length;
        bool isReserveFixed = IsReserveFixed;
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
                if (!isReserveFixed && oldReserveLength / newReserveCount >= ReserveShrinkFactorToStart)
                {
                    Debug.Assert(ReserveShrinkFactorToStart >= ReserveShrinkFactor);
                    newReserveLength = Math.Max(oldReserveLength / ReserveShrinkFactor, itemsLength);
                    newReserveLength = Math.Max(newReserveLength, newReserveCount);
                }
                else
                    newReserveLength = oldReserveLength;
            }
            else
            {
                newReserveCount = oldReserveCount;
                newReserveLength = oldReserveLength;
            }
        }

        newReserveLength = isReserveFixed ? oldReserveLength : Math.Max(newReserveLength, Math.Min(oldReserveLength, itemsLength));
        Debug.Assert(newReserveLength <= oldReserveLength);
        Debug.Assert(newReserveLength >= newReserveCount);

        if (oldReserveCount > 0)
        {
            // Clear the array only if we are not gonna replace it with a new one.
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
#endif
            {
                if (newReserveLength == oldReserveLength)
                {
                    Array.Clear(reserve_, newReserveCount, oldReserveCount - newReserveCount);
                }
            }
        }

        if (newReserveLength != oldReserveLength)
        {
            Array newReserve = new ObjectWrapper[newReserveLength];
            Array.Copy(reserve_, newReserve, newReserveCount);
        }

        reserveCount = newReserveCount;
        reserve = reserve_;
    }
}