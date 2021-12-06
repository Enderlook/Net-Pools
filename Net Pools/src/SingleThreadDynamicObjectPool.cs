using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Enderlook.Pools
{
    /// <summary>
    /// A lightweight, fast, dynamically-sized and non-thread-safe object pool to store objects.<br/>
    /// The usage of this instance in multithreading scenarios will induce undefined behaviour.
    /// </summary>
    /// <typeparam name="T">Type of object to pool</typeparam>
    internal sealed class SingleThreadDynamicObjectPool<T> : ObjectPool<T> where T : class
    {
        /// <inheritdoc cref="Container.Singlenton"/>
        public static SingleThreadDynamicObjectPool<T> Singlenton
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Container.Singlenton;
        }

        private static class Container
        {
            /// <summary>
            /// Unique instance of this type.
            /// </summary>
            // Store the shared ObjectPool in a field of its derived sealed type so the Jit can "see" the exact type
            // when the Shared property on ObjectPool<T> is inlined which will allow it to devirtualize calls made on it.
            // Also, store the shared ObjectPool in a separate class to prevent initialization of this field if not used,
            // since this field is only required on runtimes which doesn't support multithreading.
            public static readonly SingleThreadDynamicObjectPool<T> Singlenton = new SingleThreadDynamicObjectPool<T>(256);
        }

        /// <summary>
        /// Delegate that instantiates new object.
        /// </summary>
        private readonly Func<T> factory;

        /// <summary>
        /// Storage for the pool objects.
        /// </summary>
        private ObjectWrapper<T?>[] stack;

        /// <summary>
        /// Keep tracks of the amount of used slots in <see cref="stack"/>.
        /// </summary>
        private int count;

        /// <summary>
        /// Keeps tracks of the original length of <see cref="stack"/>.
        /// </summary>
        private int originalLength;

        /// <summary>
        /// The first item is stored in a dedicated field because we expect to be able to satisfy most requests from it.
        /// </summary>
        private T? firstElement;

        /// <summary>
        /// Keep record of last time <see cref="stack"/> was trimmed;
        /// </summary>
        private int stackMillisecondsTimeStamp;

        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        /// <param name="initialCapacity">Initial capacity of the pool.</param>
        /// <param name="factory">Delegate used to construct instances of the pooled objects.<br/>
        /// If no delegate is provided, a factory with the default constructor of <typeparamref name="T"/> will be used.</param>
        /// <exception cref="ArgumentOutOfRangeException">Throw when <paramref name="initialCapacity"/> is lower than 1.</exception>
        public SingleThreadDynamicObjectPool(int initialCapacity, Func<T>? factory)
        {
            if (initialCapacity == 0) Utilities.ThrowArgumentOutOfRangeException_InitialCapacityCanNotBeNegative();

            this.factory = factory ?? ObjectPoolHelper<T>.Factory;
            stack = new ObjectWrapper<T?>[initialCapacity];

            GCCallback _ = new GCCallback(this);
        }

        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        /// <param name="initialCapacity">Initial capacity of the pool.</param>
        /// <exception cref="ArgumentOutOfRangeException">Throw when <paramref name="initialCapacity"/> is lower than 1.</exception>
        public SingleThreadDynamicObjectPool(int initialCapacity) : this(initialCapacity, null) { }

        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        /// <param name="factory">Delegate used to construct instances of the pooled objects.<br/>
        /// If no delegate is provided, a factory with the default constructor of <typeparamref name="T"/> will be used.</param>
        public SingleThreadDynamicObjectPool(Func<T>? factory) : this(16, factory) { }

        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        public SingleThreadDynamicObjectPool() : this(16, null) { }

        /// <inheritdoc cref="ObjectPool{T}.ApproximateCount"/>
        public override int ApproximateCount() => count + (firstElement is null ? 0 : 1);

        /// <inheritdoc cref="ObjectPool{T}.Rent"/>
        public override T Rent()
        {
            // First, we examine the first element.
            // If that fails, we look at the remaining elements.
            T? element = firstElement;
            if (element is null)
            {
                if (count > 0)
                {
                    ref T? element_ = ref stack[--count].Value;
                    element = element_;
                    element_ = null;
                    Debug.Assert(element is not null);
                }
                else
                    element = factory();
            }
            else
                firstElement = null;

            return element;
        }

        /// <summary>
        /// Return rented object to pool.
        /// </summary>
        /// <param name="obj">Object to return.</param>
        public override void Return(T obj)
        {
            if (obj is null) return;

            // First, we examine the first element.
            // If that fails, we look at the remaining elements.
            if (firstElement is null)
            {
                firstElement = obj;
                return;
            }

            if (count >= stack.Length)
                Array.Resize(ref stack, stack.Length * 2);

            stack[count++].Value = obj;
        }

        /// <inheritdoc cref="ObjectPool{T}.Trim(bool)"/>
        public override void Trim(bool force = false)
        {
            const int StackLowAfterMilliseconds = 60 * 1000; // Trim after 60 seconds for low pressure.
            const int StackMediumAfterMilliseconds = 60 * 1000; // Trim after 60 seconds for medium pressure.
            const int StackHighTrimAfterMilliseconds = 10 * 1000; // Trim after 10 seconds for high pressure.
            const int StackLowTrimCount = 1; // Trim one item when pressure is low.
            const int StackMediumTrimCount = 2; // Trim two items when pressure is moderate.

            const float StackLowTrimPercentage = .10f; // Trim 10% of objects above original length for low pressure;
            const float StackMediumTrimPercentage = .30f; // Trim 30% of objects above original length for moderate pressure;

            const int StackShrinkFactorToStart = 4; // Reserve must be using a quarter of its capacity to shrink.
            const int StackShrinkFactor = 2; // Shrink reserve by half of its length.

            int currentMilliseconds = Environment.TickCount;

            firstElement = null; // We always trim the first element.

            ObjectWrapper<T?>[] items = stack;
            int length = items.Length;

            int arrayTrimMilliseconds;
            int arrayTrimCount;
            float reserveTrimPercentage;
            if (force)
            {
                // Forces to clear everything regardless of time.
                arrayTrimCount = length;
                arrayTrimMilliseconds = 0;
                reserveTrimPercentage = 1;
            }
            else
            {
                switch (Utilities.GetMemoryPressure())
                {
                    case Utilities.MemoryPressure.High:
                        arrayTrimCount = length;
                        arrayTrimMilliseconds = StackHighTrimAfterMilliseconds;
                        // Forces to clear everything regardless of time.
                        reserveTrimPercentage = 1;
                        break;
                    case Utilities.MemoryPressure.Medium:
                        arrayTrimCount = StackMediumTrimCount;
                        arrayTrimMilliseconds = StackMediumAfterMilliseconds;
                        reserveTrimPercentage = StackMediumTrimPercentage;
                        break;
                    default:
                        arrayTrimCount = StackLowTrimCount;
                        arrayTrimMilliseconds = StackLowAfterMilliseconds;
                        reserveTrimPercentage = StackLowTrimPercentage;
                        break;
                }
            }

            if (stackMillisecondsTimeStamp == 0)
                stackMillisecondsTimeStamp = currentMilliseconds;

            if ((currentMilliseconds - stackMillisecondsTimeStamp) > arrayTrimMilliseconds)
            {
                // We've elapsed enough time since the last clean.
                // Drop the top items so they can be collected and make the pool look a little newer.

                if (arrayTrimCount != length && reserveTrimPercentage != 1)
                {
                    int count_ = count;
                    // We remove a fixed count plus a percentage of all stored objects above the original length of the stack.
                    int toRemove_ = arrayTrimCount + (int)Math.Ceiling(Math.Max(count_ - originalLength, 0) * reserveTrimPercentage);
                    int newCount = Math.Max(count_ - toRemove_, 0);
                    int toRemove = count_ - newCount;

                    // Since the stack has a dynamic size, we shrink the stack if it gets too small.
                    if (length / count_ >= StackShrinkFactorToStart)
                    {
                        if (length <= originalLength)
                            goto simpleClean;

                        Debug.Assert(StackShrinkFactorToStart >= StackShrinkFactor);
                        int newLength = Math.Min(length / StackShrinkFactor, originalLength);
                        ObjectWrapper<T?>[] array = new ObjectWrapper<T?>[newLength];
                        Array.Copy(items, array, newCount);
                        stack = array;
                        goto next;
                    }

                    simpleClean:
                    Array.Clear(items, newCount, toRemove);
                    next:;
                    count = newCount;

                    if (toRemove_ == toRemove)
                        stackMillisecondsTimeStamp += stackMillisecondsTimeStamp / 4; // Give the remaining items a bit more time.
                }
                else
                {
#if NET6_0_OR_GREATER
                    Array.Clear(items);
#else
                    Array.Clear(items, 0, length);
#endif
                    stackMillisecondsTimeStamp = 0;
                }
            }
        }

        private sealed class GCCallback
        {
            private readonly GCHandle owner;

            public GCCallback(SingleThreadDynamicObjectPool<T> owner) => this.owner = GCHandle.Alloc(owner, GCHandleType.Weak);

            ~GCCallback()
            {
                object? owner = this.owner.Target;
                if (owner is null)
                    this.owner.Free();
                else
                {
                    Debug.Assert(owner is SingleThreadDynamicObjectPool<T>);
                    Unsafe.As<SingleThreadDynamicObjectPool<T>>(owner).Trim();
                    GC.ReRegisterForFinalize(this);
                }
            }
        }
    }
}
