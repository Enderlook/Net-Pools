using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Enderlook.Pools
{
    /// <summary>
    /// A lightweight, fast, dynamically-sized and thread-safe object pool to store value-type objects.<br/>
    /// The pool is designed for fast rent and return of elements, so during multithreading scenarios it may accidentally free unnecessary objects during return (however, this is not a fatal error).
    /// </summary>
    /// <typeparam name="T">Type of object to pool</typeparam>
    public sealed class DynamicValueObjectPool<T> : ObjectPool<T> where T : struct
    {
        /// <summary>
        /// Delegate that instantiates new object.
        /// </summary>
        private readonly Func<T> factory;

        /// <summary>
        /// Storage for the pool objects.<br/>
        /// The array is not an stack so the whole array must be traversed to find objects.
        /// </summary>
        private readonly ValueObjectWrapper<T>[] array;

        /// <summary>
        /// The first item is stored in a dedicated field because we expect to be able to satisfy most requests from it.
        /// </summary>
        private ValueObjectWrapper<T> firstElement;

        /// <summary>
        /// A dynamic-size stack reserve of objects.<br/>
        /// When <see cref="array"/> get fulls, the first half of it is emptied and its element are moved here.<br/>
        /// When <see cref="array"/> gets empty, the first half of it is fulled with elements from this reserve.<br/>
        /// Those operations are done in a batch to reduce the amount of times this requires to be acceded.<br/>
        /// However, those operations only moves the first half of the array to prevent a point where this is executed on each rent or return.
        /// </summary>
        private T[]? reserve;

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
        /// Creates a pool of objects.
        /// </summary>
        /// <param name="hotCapacity">Hot capacity of the pool.<br/>
        /// If this capacity is fulled, the pool will expand a cold region.<br/>
        /// The hot capacity should preferably be not greater than <c><see cref="Environment.ProcessorCount"/> * 2</c>.</param>
        /// <param name="initialColdCapacity">Initial capacity of the cold pool.<br/>
        /// This reserve pool is only acceded when the hot pool gets full or empty since it's slower.<br/>
        /// This pool has a dynamic size so this value represent the initial size of the pool which may enlarge or shrink over time.</param>
        /// <param name="factory">Delegate used to construct instances of the pooled objects.<br/>
        /// If no delegate is provided, a factory with the parameterless constructor (or <see langword="default"/> for value types if missing) of <typeparamref name="T"/> will be used.</param>
        /// <exception cref="ArgumentOutOfRangeException">Throw when <paramref name="hotCapacity"/> is lower than 1.</exception>
        public DynamicValueObjectPool(int hotCapacity, int initialColdCapacity, Func<T>? factory)
        {
            if (hotCapacity < 1) Utils.ThrowArgumentOutOfRangeException_HotCapacityCanNotBeLowerThanOne();
            if (initialColdCapacity < 0) Utils.ThrowArgumentOutOfRangeException_InitialColdCapacityCanNotBeNegative();

            this.factory = factory ?? ObjectPoolHelper<T>.Factory;
            array = new ValueObjectWrapper<T>[hotCapacity - 1]; // -1 due to firstElement.
            reserve = new T[initialColdCapacity];

            GCCallback _ = new(this);
        }

        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        /// <param name="hotCapacity">Hot capacity of the pool.<br/>
        /// If this capacity is fulled, the pool will expand a cold region.<br/>
        /// The hot capacity should preferably be not greater than <c><see cref="Environment.ProcessorCount"/> * 2</c>.</param>
        /// <param name="factory">Delegate used to construct instances of the pooled objects.<br/>
        /// If no delegate is provided, a factory with the parameterless constructor (or <see langword="default"/> for value types if missing) of <typeparamref name="T"/> will be used.</param>
        /// <exception cref="ArgumentOutOfRangeException">Throw when <paramref name="hotCapacity"/> is lower than 1.</exception>
        public DynamicValueObjectPool(int hotCapacity, Func<T>? factory) : this(hotCapacity, hotCapacity, factory) { }

        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        /// <param name="hotCapacity">Hot capacity of the pool. If this capacity is fulled, the pool will expand a cold region.</param>
        /// <exception cref="ArgumentOutOfRangeException">Throw when <paramref name="hotCapacity"/> is lower than 1.</exception>
        public DynamicValueObjectPool(int hotCapacity) : this(hotCapacity, hotCapacity, null) { }

        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        /// <param name="factory">Delegate used to construct instances of the pooled objects.<br/>
        /// If no delegate is provided, a factory with the parameterless constructor (or <see langword="default"/> for value types if missing) of <typeparamref name="T"/> will be used.</param>
        public DynamicValueObjectPool(Func<T>? factory) : this(Environment.ProcessorCount * 2, Environment.ProcessorCount * 2, factory) { }

        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        public DynamicValueObjectPool() : this(Environment.ProcessorCount * 2, Environment.ProcessorCount * 2, null) { }

        /// <inheritdoc cref="ObjectPool{T}.ApproximateCount"/>
        public override int ApproximateCount()
        {
            int count = firstElement.NotSynchronizedHasValue ? 1 : 0;
            ValueObjectWrapper<T>[] items = array;
            for (int i = 0; i < items.Length; i++)
                if (items[i].NotSynchronizedHasValue)
                    count++;
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
            if (!firstElement.NotSynchronizedHasValue || !firstElement.TryPopValue(out T element))
            {
                // Next, we look at all remaining elements.
                ValueObjectWrapper<T>[] items = array;

                for (int i = 0; i < items.Length; i++)
                {
                    // Note that intitial read are optimistically not synchronized. This is intentional.
                    // We will interlock only when we have a candidate.
                    // In a worst case we may miss some recently returned objects.
                    if (items[i].NotSynchronizedHasValue && items[i].TryPopValue(out element))
                        break;
                }

                // Next, we look at the reserve if it has elements.
                element = reserveCount > 0 ? FillFromReserve() : factory();
            }

            return element;
        }

        /// <summary>
        /// Return rented object to pool.<br/>
        /// Default instances are discarded.
        /// </summary>
        /// <param name="element">Object to return.</param>
        public override void Return(T element)
        {
            if (EqualityComparer<T>.Default.Equals(element, default))
                return;

            // Intentionally not using interlocked here.
            // In a worst case scenario two objects may be stored into same slot.
            // It is very unlikely to happen and will only mean that one of the objects will get collected.
            if (firstElement.NotSynchronizedHasValue || !firstElement.TrySetValue(ref element))
            {
                ValueObjectWrapper<T>[] items = array;
                for (int i = 0; i < items.Length; i++)
                {
                    if (!items[i].NotSynchronizedHasValue && items[i].TrySetValue(ref element))
                        break;
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

            firstElement.Clear(); // We always trim the first element.

            ValueObjectWrapper<T>[] items = array;
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
                    for (int i = 0; i < length; i++)
                    {
                        if (items[i].NotSynchronizedHasValue)
                        {
                            items[i].Clear();
                            if (--arrayTrimCount == 0)
                            {
                                arrayMillisecondsTimeStamp += arrayMillisecondsTimeStamp / 4; // Give the remaining items a bit more time.
                                break;
                            }
                        }
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
                    T[]? reserve_;
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
                        reserve_ = new T[reserve_.Length];

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

                        T[]? reserve_;
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
                            T[] array = new T[newLength];
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
        private T FillFromReserve()
        {
            ValueObjectWrapper<T>[] items = array;
            T[]? reserve_;
            do
            {
                reserve_ = Interlocked.Exchange(ref reserve, null);
            } while (reserve_ is null);

            int oldCount = reserveCount;
            int count = oldCount;
            if (count > 0)
            {
                T element = reserve_[--count];
                T value = default;
                bool has = false;
                for (int i = 0; i < items.Length / 2 && count > 0; i++)
                {
                    // Note that intitial read and write are optimistically not synchronized. This is intentional.
                    // We will interlock only when we have a candidate.
                    // In a worst case we may miss some recently returned objects or accidentally free objects.
                    if (!items[i].NotSynchronizedHasValue)
                    {
                        if (!has)
                            value = reserve_[--count];
                        has = !items[i].TrySetValue(ref value);
                    }
                }

                if (has)
                    reserve_[count++] = value;

                Array.Clear(reserve_, count, oldCount - count);

                reserveCount = count;
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
            ValueObjectWrapper<T>[] items = array;
            T[]? reserve_;
            do
            {
                reserve_ = Interlocked.Exchange(ref reserve, null);
            } while (reserve_ is null);

            int count = reserveCount;
            if (count + 1 + (items.Length / 2) > reserve_.Length)
                Array.Resize(ref reserve_, Math.Max(reserve_.Length * 2, 1));

            reserve_[count++] = obj;

            for (int i = 0; i < items.Length / 2 && count < reserve_.Length; i++)
            {
                // We don't use an optimistically not synchronized initial read in this part.
                // This is because we expect the majority of the array to be filled.
                // So it's not worth doing an initial read to check that.
                if (items[i].TryPopValue(out T element))
                    reserve_[count++] = element;
            }

            reserveCount = count;
            reserve = reserve_;
        }

        private sealed class GCCallback
        {
            private readonly GCHandle owner;

            public GCCallback(DynamicValueObjectPool<T> owner) => this.owner = GCHandle.Alloc(owner, GCHandleType.Weak);

            ~GCCallback()
            {
                object? owner = this.owner.Target;
                if (owner is null)
                    this.owner.Free();
                else
                {
                    Debug.Assert(owner is DynamicValueObjectPool<T>);
                    Unsafe.As<DynamicValueObjectPool<T>>(owner).Trim();
                    GC.ReRegisterForFinalize(this);
                }
            }
        }
    }
}
