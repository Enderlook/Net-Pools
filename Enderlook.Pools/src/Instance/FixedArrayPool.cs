using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Enderlook.Pools;

/// <summary>
/// A lightweight, fast, dynamically-sized and thread-safe fixed-length array pool to store objects.<br/>
/// The pool is designed for fast rent and return of arrays, so during multithreading scenarios it may accidentally free unnecessary objects during return (however, this is not a fatal error).
/// </summary>
/// <typeparam name="T">Type of object to pool</typeparam>
public sealed class FixedArrayPool<T> : ObjectPool<T[]>
{
    /// <summary>
    /// Length of arrays.
    /// </summary>
    private readonly int length;

    /// <summary>
    /// Storage for the pool objects.<br/>
    /// The array is not an stack so the whole array must be traversed to find objects.
    /// </summary>
    private readonly ObjectWrapper[] array;

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
            ObjectWrapper[] reserve_ = Utils.NullExchange(ref reserve);
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
    /// Creates a pool of fixed-length arrays.
    /// </summary>
    /// <param name="length">Length of the arrays it creates.</param>
    public FixedArrayPool(int length) : this(length, true) { }

    internal FixedArrayPool(int length, bool autoTrim)
    {
        this.length = length;
        int capacity = Environment.ProcessorCount * 2;
        array = new ObjectWrapper[capacity - 1]; // -1 due to firstElement.
        reserve = new ObjectWrapper[capacity];
        if (autoTrim)
        {
            GCCallbackObject<T[]> _ = new(this);
        }
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
    public override T[] Rent() => Rent_();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T[] Rent_()
    {
        // First, we examine the first element.
        // If that fails, we look at the remaining elements.
        // Note that intitial read are optimistically not synchronized. This is intentional.
        // We will interlock only when we have a candidate.
        // In a worst case we may miss some recently returned objects.
        T[]? element = firstElement;
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
                Debug.Assert(current.Value is null or T[]);
                element = Unsafe.As<T[]?>(current.Value);
                if (element is not null && element == Interlocked.CompareExchange(ref current.Value, null, element))
                    break;
                current = ref Unsafe.Add(ref current, 1);
            }

            // Next, we look at the reserve if it has elements.
            if (reserveCount > 0)
                element = FillFromReserve();
            else
            {
                int length = this.length;
                return length > 0 ? new T[length] : Array.Empty<T>();
            }
        }

        return element;
    }

    /// <summary>
    /// Return rented array to pool.
    /// </summary>
    /// <param name="element">Array to return.</param>
    public override void Return(T[] element) => Return_(element);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Return_(T[] element)
    {
        if (element is null) return;
        if (element.Length != length) Utils.ThrowArgumentOutOfRangeException_ArrayLength();

        // Intentionally not using interlocked here.
        // In a worst case scenario two objects may be stored into same slot.
        // It is very unlikely to happen and will only mean that one of the objects will get collected.
        if (firstElement is null)
            firstElement = element;
        else
        {
            ObjectWrapper[] items = array;
            ref ObjectWrapper current = ref Utils.GetArrayDataReference(items);
            ref ObjectWrapper end = ref Unsafe.Add(ref current, items.Length);
            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                if (current.Value is null)
                {
                    // Intentionally not using interlocked here.
                    // In a worst case scenario two objects may be stored into same slot.
                    // It is very unlikely to happen and will only mean that one of the objects will get collected.
                    current.Value = element;
                    return;
                }
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

        if (arrayMillisecondsTimeStamp == 0)
            arrayMillisecondsTimeStamp = currentMilliseconds;

        if ((currentMilliseconds - arrayMillisecondsTimeStamp) > arrayTrimMilliseconds)
        {
            // We've elapsed enough time since the last clean.
            // Drop the top items so they can be collected and make the pool look a little newer.

            if (arrayTrimCount != length)
            {
                ref ObjectWrapper current = ref Utils.GetArrayDataReference(items);
                Debug.Assert(items.Length == length);
                ref ObjectWrapper end = ref Unsafe.Add(ref current, length);
                while (Unsafe.IsAddressLessThan(ref current, ref end))
                {
                    if (current.Value is not null)
                    {
                        // Intentionally not using interlocked here.
                        current.Value = null;
                        if (--arrayTrimCount == 0)
                        {
                            arrayMillisecondsTimeStamp += arrayMillisecondsTimeStamp / 4; // Give the remaining items a bit more time.
                            break;
                        }
                    }
                    current = ref Unsafe.Add(ref current, 1);
                }
                arrayMillisecondsTimeStamp = 0;
            }
            else
            {
#if NET6_0_OR_GREATER
                Array.Clear(items);
#else
                Array.Clear(items, 0, length);
#endif
                arrayMillisecondsTimeStamp = 0;
            }
        }

        if (reserveCount == 0)
            reserveMillisecondsTimeStamp = 0;
        else
        {
            int reserveCount_;
            // Under high pressure, we don't wait time to trim, so we remove all objects in reserve.
            if (reserveTrimPercentage == 1)
            {
                Debug.Assert(reserveTrimMilliseconds == 0);
                ObjectWrapper[]? reserve_;
                do
                {
                    reserve_ = Interlocked.Exchange(ref reserve, null);
                } while (reserve_ is null);
                reserveCount_ = 0;

                if (reserve_.Length <= items.Length)
                {
#if NET6_0_OR_GREATER
                    Array.Clear(reserve_);
#else
                    Array.Clear(reserve_, 0, reserve_.Length);
#endif
                }
                else
                    reserve_ = new ObjectWrapper[reserve_.Length];

                reserveMillisecondsTimeStamp = 0;
                reserveCount = reserveCount_;
                reserve = reserve_;
            }
            else
            {
                if (reserveMillisecondsTimeStamp == 0)
                    reserveMillisecondsTimeStamp = currentMilliseconds;

                if ((currentMilliseconds - reserveMillisecondsTimeStamp) > reserveTrimMilliseconds)
                {
                    // Otherwise, remove a percentage of all stored objects in the reserve, based on how long was the last trimming.
                    // This time is approximate, with the time set not when the element is stored but when we see it during a Trim,
                    // so it takes at least two Trim calls (and thus two gen2 GCs) to drop elements, unless we're in high memory pressure.

                    ObjectWrapper[]? reserve_;
                    do
                    {
                        reserve_ = Interlocked.Exchange(ref reserve, null);
                    } while (reserve_ is null);
                    reserveCount_ = reserveCount;

                    int toRemove = (int)Math.Ceiling(reserveCount_ * reserveTrimPercentage);
                    int newReserveCount = Math.Max(reserveCount_ - toRemove, 0);
                    toRemove = reserveCount_ - newReserveCount;
                    int reserveLength = reserve_.Length;
                    reserveCount_ = newReserveCount;

                    // Since the global reserve has a dynamic size, we shrink the reserve if it gets too small.

                    if (reserveLength / reserveCount_ >= ReserveShrinkFactorToStart)
                    {
                        if (reserveLength <= items.Length)
                            goto simpleClean;

                        Debug.Assert(ReserveShrinkFactorToStart >= ReserveShrinkFactor);
                        int newLength = Math.Min(reserveLength / ReserveShrinkFactor, items.Length);
                        ObjectWrapper[] array = new ObjectWrapper[newLength];
                        Array.Copy(reserve_, array, newReserveCount);
                        reserve_ = array;
                        goto next2;
                    }
                simpleClean:
                    Array.Clear(reserve_, newReserveCount, toRemove);
                next2:;

                    reserveCount = reserveCount_;
                    reserve = reserve_;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T[] FillFromReserve()
    {
        ObjectWrapper[] items = array;
        ObjectWrapper[]? reserve_ = Utils.NullExchange(ref reserve);

        int reserveCount_ = reserveCount;
        if (reserveCount_ > 0)
        {
            int oldReserveCount = reserveCount_;

            ref ObjectWrapper startReserve = ref Utils.GetArrayDataReference(reserve_);
            ref ObjectWrapper currentReserve = ref Unsafe.Add(ref startReserve, reserveCount_ - 1);

#if DEBUG
            int i = 1;
#endif

            object value = currentReserve.Value!;
            Debug.Assert(value is not null);
            currentReserve = ref Unsafe.Subtract(ref currentReserve, 1);
            Debug.Assert(--reserveCount_ >= 0);

            ref ObjectWrapper currentItem = ref Utils.GetArrayDataReference(items);
            ref ObjectWrapper endItem = ref Unsafe.Add(ref currentItem, Math.Min(oldReserveCount - 1, items.Length / 2));
            while (Unsafe.IsAddressLessThan(ref currentItem, ref endItem))
            {
                // Note that intitial read and write are optimistically not synchronized. This is intentional.
                // In a worst case we may miss some recently returned objects or accidentally free objects.
                if (currentItem.Value is null)
                {
#if DEBUG
                    i++;
#endif
                    currentReserve = ref Unsafe.Subtract(ref currentReserve, 1);
                    Debug.Assert(--reserveCount_ >= 0);
                }
                currentItem = ref Unsafe.Add(ref currentItem, 1);
            }

            int newReserveCount = (int)((long)Unsafe.ByteOffset(ref startReserve, ref currentReserve) / Unsafe.SizeOf<ObjectWrapper>()) + 1;
            Debug.Assert(newReserveCount == reserveCount_);
#if DEBUG
            Debug.Assert(i == oldReserveCount - newReserveCount);
#endif
            Array.Clear(reserve_, newReserveCount, oldReserveCount - newReserveCount);
            reserveCount = newReserveCount;
            reserve = reserve_;
            Debug.Assert(value is T[]);
            return Unsafe.As<T[]>(value);
        }

        reserve = reserve_;

        int length = this.length;
        if (length != 0)
        {
#if NET5_0_OR_GREATER
            return GC.AllocateUninitializedArray<T>(length);
#else
            return new T[length];
#endif
        }
        else
        {
            return Array.Empty<T>();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SendToReserve(T[] obj)
    {
        if (length == 0) return;

        ObjectWrapper[] items = array;
        ObjectWrapper[]? reserve_ = Utils.NullExchange(ref reserve);

        int currentReserveCount = reserveCount;
        int newCount = currentReserveCount + 1 + (items.Length / 2);
        if (newCount > reserve_.Length)
        {
            if (IsReserveDynamic)
                Array.Resize(ref reserve_, Math.Max(newCount, Math.Max(reserve_.Length * 2, 1)));
            else if (currentReserveCount + 1 == reserve_.Length)
            {
                reserve = reserve_;
                return;
            }
        }

        int newReserveCount = ObjectPoolHelper.SendToReserve_(obj, items, reserve_, ref reserveCount);

        reserveCount = newReserveCount;
        reserve = reserve_;
    }
}
