using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Enderlook.Pools;

/// <summary>
/// A fast and thread-safe object pool to store a large amount of objects.
/// </summary>
/// <typeparam name="T">Type of object to pool.</typeparam>
internal sealed class ThreadLocalOverPerCoreLockedStacksValueObjectPool<T> : ObjectPool<T> where T : struct
{
    // Inspired from https://source.dot.net/#System.Private.CoreLib/TlsOverPerCoreLockedStacksArrayPool.cs

    /// <summary>
    /// Maximum length of <see cref="perCoreStacks"/> to use.
    /// </summary>
    private const int MaximumPerCoreStack = 64; // Selected to avoid needing to worry about processor groups.

    /// <summary>
    /// The maximum number of objects to store in each per-core stack.
    /// </summary>
    private const int MaxObjectsPerCore = 128;

    /// <summary>
    /// The initial capacity of <see cref="globalReserve"/>.
    /// </summary>
    private const int InitialGlobalReserveCapacity = 256;

    /// <summary>
    /// Number of locked stacks to employ.
    /// </summary>
    private static readonly int PerCoreStacksCount = Math.Min(Environment.ProcessorCount, MaximumPerCoreStack);

    /// <summary>
    /// A per-thread element for better cache.
    /// </summary>
    [ThreadStatic]
    private static ThreadLocalElement? threadLocalElement;

    /// <summary>
    /// Used to keep tack of all thread local objects for trimming if needed.
    /// </summary>
    private static GCHandle[]? allThreadLocalElements = new GCHandle[Environment.ProcessorCount];

    /// <summary>
    /// Keep tracks of the amount of used slots in <see cref="allThreadLocalElements"/>.
    /// </summary>
    private static int allThreadLocalElementsCount;

    /// <summary>
    /// An array of per-core objects.<br/>
    /// The slots are lazily initialized.
    /// </summary>
    private static readonly PerCoreStack[] perCoreStacks = new PerCoreStack[PerCoreStacksCount];

    /// <summary>
    /// A global dynamic-size reserve of elements.<br/>
    /// When all <see cref="perCoreStacks"/> get fulls, one of them is emptied and all its objects are moved here.<br/>
    /// When all <see cref="perCoreStacks"/> get empty, one of them is fulled with objects from this reserve.<br/>
    /// Those operations are done in a batch to reduce the amount of times this requires to be acceded.
    /// </summary>
    private static T[]? globalReserve = new T[MaxObjectsPerCore];

    /// <summary>
    /// Keep tracks of the amount of used slots in <see cref="globalReserve"/>.
    /// </summary>
    private static int globalReserveCount;

    /// <summary>
    /// Keep record of last time <see cref="globalReserve"/> was trimmed;
    /// </summary>
    private static int globalReserveMillisecondsTimeStamp;

    static ThreadLocalOverPerCoreLockedStacksValueObjectPool()
    {
        for (int i = 0; i < perCoreStacks.Length; i++)
            perCoreStacks[i] = new PerCoreStack(new T[MaxObjectsPerCore]);
        GCCallback _ = new();
    }

    /// <inheritdoc cref="ObjectPool{T}.ApproximateCount"/>
    public override int ApproximateCount()
    {
        int count = 0;
        for (int i = 0; i < perCoreStacks.Length; i++)
            count += perCoreStacks[i].GetCount();

        T[]? globalReserve_;
        do
        {
            globalReserve_ = Interlocked.Exchange(ref globalReserve, null);
        } while (globalReserve_ is null);
        count += globalReserveCount;
        globalReserve = globalReserve_;

        return count;
    }

    /// <inheritdoc cref="ObjectPool{T}.Rent"/>
    public override T Rent()
    {
        // First, try to get an element from the thread local field if possible.
        ThreadLocalElement? threadLocalElement_ = threadLocalElement;
        if (threadLocalElement_ is not null)
        {
            T? element = threadLocalElement_.Value;
            if (element is T element_)
            {
                threadLocalElement_.Value = null;
                return element_;
            }
        }

        // Next, try to get an element from one of the per-core stacks.
        PerCoreStack[] perCoreStacks_ = perCoreStacks;
        ref PerCoreStack perCoreStacks_Root = ref Utils.GetArrayDataReference(perCoreStacks_);
        // Try to pop from the associated stack first.
        // If that fails, try with other stacks.
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        int currentProcessorId = Thread.GetCurrentProcessorId();
#else
        int currentProcessorId = Thread.CurrentThread.ManagedThreadId; // TODO: This is probably a bad idea.
#endif
        int index = (int)((uint)currentProcessorId % (uint)PerCoreStacksCount);
        for (int i = 0; i < perCoreStacks_.Length; i++)
        {
            Debug.Assert(index < perCoreStacks_.Length);
            // TODO: This Unsafe.Add could be improved to avoid the under the hood mulitplication (`base + offset * size` and just do `base + offset`).
            if (Unsafe.Add(ref perCoreStacks_Root, index).TryPop(out T element))
                return element;

            if (++index == perCoreStacks_.Length)
                index = 0;
        }

        // Next, try to fill a per-core stack with objects from the global reserve.
        if (globalReserveCount > 0)
            return FillFromGlobalReserve();

        // Finally, instantiate a new object.
        return ObjectPoolHelper<T>.Create();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static T FillFromGlobalReserve()
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            int currentProcessorId = Thread.GetCurrentProcessorId();
#else
            int currentProcessorId = Thread.CurrentThread.ManagedThreadId; // TODO: This is probably a bad idea.
#endif
            int index = (int)((uint)currentProcessorId % (uint)PerCoreStacksCount);

            if (Unsafe.Add(ref Utils.GetArrayDataReference(perCoreStacks), index).FillFromGlobalReserve(out T element))
                return element;
            // Finally, instantiate a new object.
            return ObjectPoolHelper<T>.Create();
        }
    }

    /// <summary>
    /// Return rented object to pool.<br/>
    /// If the pool is full, the object will be discarded.<br/>
    /// Default instances are discarded.
    /// </summary>
    /// <param name="element">Object to return.</param>
    public override void Return(T element)
    {
        if (EqualityComparer<T>.Default.Equals(element, default))
            return;

        // Store the element into the thread local field.
        // If there's already an object in it, push that object down into the per-core stacks,
        // preferring to keep the latest one in thread local field for better locality.
        ThreadLocalElement threadLocalElement_ = threadLocalElement ?? ThreadLocalOverPerCoreLockedStacksValueObjectPool<T>.InitializeThreadLocalElement();
        T? previous = threadLocalElement_.Value;
        threadLocalElement_.Value = element;
        threadLocalElement_.MillisecondsTimeStamp = 0;
        if (previous is T previous_)
        {
            // Try to store the object from one of the per-core stacks.
            PerCoreStack[] perCoreStacks_ = perCoreStacks;
            ref PerCoreStack perCoreStacks_Root = ref Utils.GetArrayDataReference(perCoreStacks_);
            // Try to push from the associated stack first.
            // If that fails, try with other stacks.
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            int currentProcessorId = Thread.GetCurrentProcessorId();
#else
            int currentProcessorId = Thread.CurrentThread.ManagedThreadId; // TODO: This is probably a bad idea.
#endif
            int index = (int)((uint)currentProcessorId % (uint)PerCoreStacksCount);
            for (int i = 0; i < perCoreStacks_.Length; i++)
            {
                Debug.Assert(index < perCoreStacks_.Length);
                if (Unsafe.Add(ref perCoreStacks_Root, index).TryPush(previous_))
                    return;

                if (++index == perCoreStacks_.Length)
                    index = 0;
            }

            // Next, transfer a per-core stack to the global reserve.
            Debug.Assert(index < perCoreStacks_.Length);
            Unsafe.Add(ref perCoreStacks_Root, index).MoveToGlobalReserve(previous_);
        }
    }

    /// <inheritdoc cref="ObjectPool{T}.Trim(bool)"/>
    public override void Trim(bool force = false)
    {
        // This method does nothing to prevent user trying to clear the singlenton.
    }

    /// <inheritdoc cref="ObjectPool{T}.Trim(bool)"/>
    private static void Trim_(bool force = false)
    {
        const int PerCoreLowTrimAfterMilliseconds = 60 * 1000; // Trim after 60 seconds for low pressure.
        const int PerCoreMediumTrimAfterMilliseconds = 60 * 1000; // Trim after 60 seconds for moderate pressure.
        const int PerCoreHighTrimAfterMilliseconds = 10 * 1000; // Trim after 10 seconds for high pressure.
        const int PerCoreLowTrimCount = 1; // Trim 1 item when pressure is low.
        const int PerCoreMediumTrimCount = 2; // Trim 2 items when pressure is moderate.
        const int PerCoreHighTrimCount = MaxObjectsPerCore; // Trim all items when pressure is high.

        const int ThreadLocalLowMilliseconds = 30 * 1000; // Trim after 30 seconds for moderate pressure.
        const int ThreadLocalMediumMilliseconds = 15 * 1000; // Trim after 15 seconds for low pressure.

        const int ReserveLowTrimAfterMilliseconds = 90 * 1000; // Trim after 90 seconds for low pressure.
        const int ReserveMediumTrimAfterMilliseconds = 45 * 1000; // Trim after 45 seconds for low pressure.
        const float ReserveLowTrimPercentage = .10f; // Trim 10% of elements for low pressure;
        const float ReserveMediumTrimPercentage = .30f; // Trim 30% of elements for moderate pressure;

        int currentMilliseconds = Environment.TickCount;

        Utils.MemoryPressure memoryPressure;
        int perCoreTrimMilliseconds;
        int perCoreTrimCount;
        uint threadLocalTrimMilliseconds;
        int globalTrimMilliseconds;
        float globalTrimPercentage;
        if (force)
        {
            memoryPressure = Utils.MemoryPressure.High;
            perCoreTrimCount = PerCoreHighTrimCount;
            // Forces to clear everything regardless of time.
            perCoreTrimMilliseconds = 0;
            threadLocalTrimMilliseconds = 0;
            globalTrimMilliseconds = 0;
            globalTrimPercentage = 1;
        }
        else
        {
            memoryPressure = Utils.GetMemoryPressure();
            switch (memoryPressure)
            {
                case Utils.MemoryPressure.High:
                    perCoreTrimCount = PerCoreHighTrimCount;
                    perCoreTrimMilliseconds = PerCoreHighTrimAfterMilliseconds;
                    // Forces to clear everything regardless of time.
                    threadLocalTrimMilliseconds = 0;
                    globalTrimMilliseconds = 0;
                    globalTrimPercentage = 1;
                    break;
                case Utils.MemoryPressure.Medium:
                    perCoreTrimCount = PerCoreMediumTrimCount;
                    perCoreTrimMilliseconds = PerCoreMediumTrimAfterMilliseconds;
                    threadLocalTrimMilliseconds = ThreadLocalMediumMilliseconds;
                    globalTrimMilliseconds = ReserveMediumTrimAfterMilliseconds;
                    globalTrimPercentage = ReserveMediumTrimPercentage;
                    break;
                default:
                    Debug.Assert(memoryPressure == Utils.MemoryPressure.Low);
                    perCoreTrimCount = PerCoreLowTrimCount;
                    perCoreTrimMilliseconds = PerCoreLowTrimAfterMilliseconds;
                    threadLocalTrimMilliseconds = ThreadLocalLowMilliseconds;
                    globalTrimMilliseconds = ReserveLowTrimAfterMilliseconds;
                    globalTrimPercentage = ReserveLowTrimPercentage;
                    break;
            }
        }

        {
            PerCoreStack[] perCoreStacks_ = perCoreStacks;
            ref PerCoreStack current = ref Utils.GetArrayDataReference(perCoreStacks_);
            ref PerCoreStack end = ref Unsafe.Add(ref current, perCoreStacks_.Length);
            // Trim each of the per-core stacks.
            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                current.TryTrim(currentMilliseconds, perCoreTrimMilliseconds, perCoreTrimCount);
                current = ref Unsafe.Add(ref current, 1);
            }
        }

        {
            // Trim each of the thread local fields.
            // Note that threads may be modifying their thread local fields concurrently with this trimming happening.
            // We do not force synchronization with those operations, so we accept the fact
            // that we may potentially trim an object we didn't need to.
            // Both of these should be rare occurrences.

            GCHandle[]? allThreadLocalElements_;
            do
            {
                allThreadLocalElements_ = Interlocked.Exchange(ref allThreadLocalElements, null);
            } while (allThreadLocalElements_ is null);
            int length = allThreadLocalElementsCount;

            ref GCHandle start = ref Utils.GetArrayDataReference(allThreadLocalElements_);
            ref GCHandle current = ref start;
            ref GCHandle newCurrent = ref start;
#if DEBUG
            int count = 0;
#endif
            ref GCHandle end = ref Unsafe.Add(ref start, length);

            // Under high pressure, we don't wait time to trim, so we release all thread locals.
            if (threadLocalTrimMilliseconds == 0)
            {
                while (Unsafe.IsAddressLessThan(ref current, ref end))
                {
                    GCHandle handle = current;
                    object? target = handle.Target;
                    if (target is null)
                    {
                        handle.Free();
                        current = ref Unsafe.Add(ref current, 1);
                        continue;
                    }
                    Debug.Assert(target is ThreadLocalElement);
                    Unsafe.As<ThreadLocalElement>(target).Clear();
                    Debug.Assert(Unsafe.IsAddressLessThan(ref newCurrent, ref end));
                    newCurrent = handle;
#if DEBUG
                    Debug.Assert(count++ < allThreadLocalElements_.Length);
#endif
                    newCurrent = ref Unsafe.Add(ref newCurrent, 1);
                    current = ref Unsafe.Add(ref current, 1);
                }
            }
            else
            {
                // Otherwise, release thread locals based on how long we've observed them to be stored.
                // This time is approximate, with the time set not when the object is stored but when we see it during a Trim,
                // so it takes at least two Trim calls (and thus two gen2 GCs) to drop an object, unless we're in high memory pressure.

                while (Unsafe.IsAddressLessThan(ref current, ref end))
                {
                    GCHandle handle = current;
                    object? target = handle.Target;
                    if (target is null)
                    {
                        handle.Free();
                        current = ref Unsafe.Add(ref current, 1);
                        continue;
                    }
                    Debug.Assert(target is ThreadLocalElement);
                    Unsafe.As<ThreadLocalElement>(target).Trim(currentMilliseconds, threadLocalTrimMilliseconds);
                    Debug.Assert(Unsafe.IsAddressLessThan(ref newCurrent, ref end));
                    newCurrent = handle;
#if DEBUG
                    Debug.Assert(count++ < allThreadLocalElements_.Length);
#endif
                    newCurrent = ref Unsafe.Add(ref newCurrent, 1);
                    current = ref Unsafe.Add(ref current, 1);
                }
            }

            int count_ = (int)Unsafe.ByteOffset(ref start, ref newCurrent) / Unsafe.SizeOf<GCHandle>();
#if DEBUG
            Debug.Assert(count_ == count);
#endif
            allThreadLocalElementsCount = count_;
            allThreadLocalElements = allThreadLocalElements_;
        }

        T[]? globalReserve_;
        do
        {
            globalReserve_ = Interlocked.Exchange(ref globalReserve, null);
        } while (globalReserve_ is null);
        int globalCount = globalReserveCount;

        if (globalCount == 0)
            globalReserveMillisecondsTimeStamp = 0;
        else
        {
            // Under high pressure, we don't wait time to trim, so we remove all objects in reserve.
            if (globalTrimPercentage == 1)
            {
                Debug.Assert(globalTrimMilliseconds == 0);
                globalCount = 0;
                if (globalReserve_.Length <= MaxObjectsPerCore)
                {
#if NET6_0_OR_GREATER
                    Array.Clear(globalReserve_);
#else
                    Array.Clear(globalReserve_, 0, globalReserve_.Length);
#endif
                }
                else
                    globalReserve_ = new T[InitialGlobalReserveCapacity];
                globalReserveMillisecondsTimeStamp = 0;
            }
            else
            {
                if (globalReserveMillisecondsTimeStamp == 0)
                    globalReserveMillisecondsTimeStamp = currentMilliseconds;

                if ((currentMilliseconds - globalReserveMillisecondsTimeStamp) > globalTrimMilliseconds)
                {
                    // Otherwise, remove a percentage of all stored objects in the reserve, based on how long was the last trimming.
                    // This time is approximate, with the time set not when the object is stored but when we see it during a Trim,
                    // so it takes at least two Trim calls (and thus two gen2 GCs) to drop objects, unless we're in high memory pressure.

                    int toRemove = (int)Math.Ceiling(globalCount * globalTrimPercentage);
                    int newGlobalCount = Math.Max(globalCount - toRemove, 0);
                    toRemove = globalCount - newGlobalCount;
                    int globalLength = globalReserve_.Length;
                    globalCount = newGlobalCount;

                    // Since the global reserve has a dynamic size, we shrink the reserve if it gets too small.
                    if (globalLength / newGlobalCount >= 4)
                    {
                        if (globalLength <= InitialGlobalReserveCapacity)
                            goto simpleClean;
                        else
                        {
                            int newLength = globalLength / 2;
                            T[] array = new T[newLength];
                            Array.Copy(globalReserve_, array, newGlobalCount);
                            globalReserve_ = array;
                            goto next;
                        }
                    }
                simpleClean:
                    Array.Clear(globalReserve_, newGlobalCount, toRemove);
                next:;
                }
            }
        }

        globalReserveCount = globalCount;
        globalReserve = globalReserve_;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ThreadLocalElement InitializeThreadLocalElement()
    {
        ThreadLocalElement slot = new();
        threadLocalElement = slot;

        GCHandle[]? allThreadLocalElements_;
        do
        {
            allThreadLocalElements_ = Interlocked.Exchange(ref allThreadLocalElements, null);
        } while (allThreadLocalElements_ is null);

        int count_ = allThreadLocalElementsCount;
        if (unchecked((uint)count_ >= (uint)allThreadLocalElements_.Length))
        {
            ref GCHandle current = ref Utils.GetArrayDataReference(allThreadLocalElements_);
            ref GCHandle end = ref Unsafe.Add(ref current, allThreadLocalElements_.Length);

            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                GCHandle handle = current;
                if (!handle.IsAllocated)
                    Console.WriteLine(1);
                Debug.Assert(handle.IsAllocated);
                object? target = handle.Target;
                if (target is null)
                {
                    handle.Free();
                    current = GCHandle.Alloc(slot, GCHandleType.Weak);
                    goto end;
                }
                current = ref Unsafe.Add(ref current, 1);
            }

            if (count_ < allThreadLocalElements_.Length)
                Array.Clear(allThreadLocalElements_, count_, allThreadLocalElements_.Length - count_);
            else
                Array.Resize(ref allThreadLocalElements_, allThreadLocalElements_.Length * 2);
        }

        Debug.Assert(count_ < allThreadLocalElements_.Length);
        Unsafe.Add(ref Utils.GetArrayDataReference(allThreadLocalElements_), count_) = GCHandle.Alloc(slot, GCHandleType.Weak);
        allThreadLocalElementsCount = count_ + 1;

    end:
        allThreadLocalElements = allThreadLocalElements_;
        return slot;
    }

    private sealed class ThreadLocalElement
    {
        public T? Value;
        public int MillisecondsTimeStamp;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T? ReplaceWith(T value)
        {
            T? previous = Value;
            Value = value;
            MillisecondsTimeStamp = 0;
            return previous;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Clear()
        {
            Value = null;
            MillisecondsTimeStamp = 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Trim(int currentMilliseconds, uint millisecondsThreshold)
        {
            // We treat 0 to mean it hasn't yet been seen in a Trim call.
            // In the very rare case where Trim records 0, it'll take an extra Trim call to remove the object.
            int lastSeen = MillisecondsTimeStamp;
            if (lastSeen == 0)
                MillisecondsTimeStamp = currentMilliseconds;
            else if ((currentMilliseconds - lastSeen) >= millisecondsThreshold)
            {
                // Time noticeably wrapped, or we've surpassed the threshold.
                // Clear out the array.
                Value = null;
            }
        }
    }

    private struct PerCoreStack
    {
        private readonly T[] array;
        private int count;
        private int millisecondsTimeStamp;

        public PerCoreStack(T[] array)
        {
            this.array = array;
            count = 0;
            millisecondsTimeStamp = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetCount()
        {
            int count_;
            do
            {
                count_ = count;
            } while (count_ == -1);
            return count_;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryPush(T element)
        {
            T[] items = array;

            int count_;
            do
            {
                count_ = Interlocked.Exchange(ref count, -1);
            } while (count_ == -1);

            bool enqueued = false;
            if (unchecked((uint)count_ < (uint)items.Length))
            {
                if (count_ == 0)
                {
                    // Reset the time stamp now that we're transitioning from empty to non-empty.
                    // Trim will see this as 0 and initialize it to the current time when Trim is called.
                    millisecondsTimeStamp = 0;
                }

                Debug.Assert(count_ < items.Length);
                Unsafe.Add(ref Utils.GetArrayDataReference(items), count_++) = element;
                enqueued = true;
            }

            count = count_;
            return enqueued;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryPop(out T element)
        {
            T[] items = array;

            int count_;
            do
            {
                count_ = Interlocked.Exchange(ref count, -1);
            } while (count_ == -1);

            int newCount = count_ - 1;
            if (unchecked((uint)newCount < (uint)items.Length))
            {
                Debug.Assert(newCount < items.Length);
                ref T slot = ref Unsafe.Add(ref Utils.GetArrayDataReference(items), newCount);
                element = slot;
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
                if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
#endif
                    slot = default;
                count = newCount;
                return true;
            }

#if NET5_0_OR_GREATER
            Unsafe.SkipInit(out element);
#else
            element = default;
#endif
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool FillFromGlobalReserve(out T element)
        {
            int count_;
            do
            {
                count_ = Interlocked.Exchange(ref count, -1);
            } while (count_ == -1);

            T[]? globalReserve_;
            do
            {
                globalReserve_ = Interlocked.Exchange(ref globalReserve, null);
            } while (globalReserve_ is null);

            int globalCount = globalReserveCount;
            bool found;
            if (globalCount > 0)
            {
                Debug.Assert(globalCount - 1 < globalReserve_.Length);
                element = Unsafe.Add(ref Utils.GetArrayDataReference(globalReserve_), --globalCount);
                found = true;

                T[] items = array;

                int length = Math.Min(MaxObjectsPerCore - count_, globalCount);
                int start = globalCount - length;
                Array.Copy(globalReserve_, start, items, count_, length);
                Array.Clear(globalReserve_, start, length);

                globalCount = start;
                count_ += length;

                globalReserveCount = globalCount;
            }
            else
            {
                found = false;
#if NET5_0_OR_GREATER
                Unsafe.SkipInit(out element);
#else
                element = default;
#endif
            }

            globalReserve = globalReserve_;
            count = count_;
            return found;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MoveToGlobalReserve(T obj)
        {
            int count_;
            do
            {
                count_ = Interlocked.Exchange(ref count, -1);
            } while (count_ == -1);

            T[]? globalReserve_;
            do
            {
                globalReserve_ = Interlocked.Exchange(ref globalReserve, null);
            } while (globalReserve_ is null);

            T[] items = array;
            int amount = count_ + 1;
            int globalCount = globalReserveCount;
            int newGlobalCount = globalCount + amount;
            if (unchecked((uint)newGlobalCount >= (uint)globalReserve_.Length))
                Array.Resize(ref globalReserve_, globalReserve_.Length * 2);
            Array.Copy(items, 0, globalReserve_, globalCount, count_);
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
#endif
#if NET6_0_OR_GREATER
                Array.Clear(items);
#else
                Array.Clear(items, 0, items.Length);
#endif
            globalCount += count_;
            count_ = 0;
            Debug.Assert(globalCount < globalReserve_.Length);
            Unsafe.Add(ref Utils.GetArrayDataReference(globalReserve_), globalCount++) = obj;

            globalReserveCount = globalCount;
            globalReserve = globalReserve_;
            count = count_;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TryTrim(int currentMilliseconds, int trimMilliseconds, int trimCount)
        {
            if (count == 0)
                return;

            T[] items = array;

            int count_;
            do
            {
                count_ = Interlocked.Exchange(ref count, -1);
            } while (count_ == -1);

            if (count_ == 0)
                goto end;

            if (millisecondsTimeStamp == 0)
                millisecondsTimeStamp = currentMilliseconds;

            if ((currentMilliseconds - millisecondsTimeStamp) <= trimMilliseconds)
                goto end;

            // We've elapsed enough time since the first item went into the stack.
            // Drop the top item so it can be collected and make the stack look a little newer.

            Array.Clear(items, 0, Math.Min(count_, trimCount));
            count_ = Math.Max(count_ - trimCount, 0);

            millisecondsTimeStamp = count_ > 0 ?
                millisecondsTimeStamp + (trimMilliseconds / 4) // Give the remaining items a bit more time.
                : 0;

        end:
            count = count_;
        }
    }

    private sealed class GCCallback
    {
        ~GCCallback()
        {
            ThreadLocalOverPerCoreLockedStacksValueObjectPool<T>.Trim_();
            GC.ReRegisterForFinalize(this);
        }
    }
}
