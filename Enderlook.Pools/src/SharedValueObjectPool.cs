using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Enderlook.Pools;

/// <summary>
/// A fast and thread-safe object pool to store a large amount of objects.
/// </summary>
/// <typeparam name="T">Type of object to pool.</typeparam>
internal sealed class SharedValueObjectPool<
#if NET5_0_OR_GREATER
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
#endif
T> : ObjectPool<T> where T : struct
{
    // Inspired from https://source.dot.net/#System.Private.CoreLib/TlsOverPerCoreLockedStacksArrayPool.cs

    /// <summary>
    /// A per-thread element for better cache.
    /// </summary>
    [ThreadStatic]
    private static SharedThreadLocalElement<T?>? threadLocalElement;

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
    private static readonly SharedPerCoreStack<T>[] perCoreStacks = new SharedPerCoreStack<T>[Utils.PerCoreStacksCount];

    /// <summary>
    /// A global dynamic-size reserve of elements.<br/>
    /// When all <see cref="perCoreStacks"/> get fulls, one of them is emptied and all its objects are moved here.<br/>
    /// When all <see cref="perCoreStacks"/> get empty, one of them is fulled with objects from this reserve.<br/>
    /// Those operations are done in a batch to reduce the amount of times this requires to be acceded.
    /// </summary>
    private static T[]? globalReserve = new T[Utils.MaxObjectsPerCore];

    /// <summary>
    /// Keep tracks of the amount of used slots in <see cref="globalReserve"/>.
    /// </summary>
    private static int globalReserveCount;

    /// <summary>
    /// Keep record of last time <see cref="globalReserve"/> was trimmed;
    /// </summary>
    private static int globalReserveMillisecondsTimeStamp;

    static SharedValueObjectPool()
    {
        for (int i = 0; i < perCoreStacks.Length; i++)
            perCoreStacks[i] = new SharedPerCoreStack<T>(new T[Utils.MaxObjectsPerCore]);
    }

    /// <inheritdoc cref="ObjectPool{T}.ApproximateCount"/>
    public override int ApproximateCount()
    {
        SharedPerCoreStack<T>[] perCoreStacks_ = perCoreStacks;
        ref SharedPerCoreStack<T> current = ref Utils.GetArrayDataReference(perCoreStacks_);
        ref SharedPerCoreStack<T> end = ref Unsafe.Add(ref current, perCoreStacks_.Length);
        int count = 0;
        while (Unsafe.IsAddressLessThan(ref current, ref end))
        {
            count += current.GetCount();
            current = ref Unsafe.Add(ref current, 1);
        }

        SpinWait spinWait = new();
        T[]? globalReserve_;
        while (true)
        {
            globalReserve_ = Interlocked.Exchange(ref globalReserve, null);
            if (globalReserve_ is not null)
                break;
            spinWait.SpinOnce();
        }
        count += globalReserveCount;
        globalReserve = globalReserve_;

        return count;
    }

    /// <inheritdoc cref="ObjectPool{T}.Rent"/>
    public override T Rent()
    {
        // First, try to get an element from the thread local field if possible.
        SharedThreadLocalElement<T?>? threadLocalElement_ = threadLocalElement;
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
        SharedPerCoreStack<T>[] perCoreStacks_ = perCoreStacks;
        ref SharedPerCoreStack<T> perCoreStacks_Root = ref Utils.GetArrayDataReference(perCoreStacks_);
        // Try to pop from the associated stack first.
        // If that fails, try with other stacks.
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        int currentProcessorId = Thread.GetCurrentProcessorId();
#else
        int currentProcessorId = Thread.CurrentThread.ManagedThreadId; // TODO: This is probably a bad idea.
#endif
        int index = (int)((uint)currentProcessorId % (uint)Utils.PerCoreStacksCount);
        for (int i = 0; i < perCoreStacks_.Length; i++)
        {
            Debug.Assert(index < perCoreStacks_.Length);
            // TODO: This Unsafe.Add could be improved to avoid the under the hood multiplication (`base + offset * size` and just do `base + offset`).
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
            int index = (int)((uint)currentProcessorId % (uint)Utils.PerCoreStacksCount);

            if (Unsafe.Add(ref Utils.GetArrayDataReference(perCoreStacks), index).FillFromGlobalReserve(out T element, ref globalReserve, ref globalReserveCount))
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
        SharedThreadLocalElement<T?> threadLocalElement_ = threadLocalElement ?? InitializeThreadLocalElement();
        T? previous = threadLocalElement_.Value;
        threadLocalElement_.Value = element;
        threadLocalElement_.MillisecondsTimeStamp = 0;
        if (previous is T previous_)
        {
            // Try to store the object from one of the per-core stacks.
            SharedPerCoreStack<T>[] perCoreStacks_ = perCoreStacks;
            ref SharedPerCoreStack<T> perCoreStacks_Root = ref Utils.GetArrayDataReference(perCoreStacks_);
            // Try to push from the associated stack first.
            // If that fails, try with other stacks.
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            int currentProcessorId = Thread.GetCurrentProcessorId();
#else
            int currentProcessorId = Thread.CurrentThread.ManagedThreadId; // TODO: This is probably a bad idea.
#endif
            int index = (int)((uint)currentProcessorId % (uint)Utils.PerCoreStacksCount);
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
            Unsafe.Add(ref perCoreStacks_Root, index).MoveToGlobalReserve(previous_, ref globalReserve, ref globalReserveCount);
        }
    }

    /// <inheritdoc cref="ObjectPool{T}.Trim(bool)"/>
    public override void Trim(bool force = false) => Trim_(force);

    /// <inheritdoc cref="ObjectPool{T}.Trim(bool)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Trim_(bool force = false)
    {
        const int PerCoreLowTrimAfterMilliseconds = 60 * 1000; // Trim after 60 seconds for low pressure.
        const int PerCoreMediumTrimAfterMilliseconds = 60 * 1000; // Trim after 60 seconds for moderate pressure.
        const int PerCoreHighTrimAfterMilliseconds = 10 * 1000; // Trim after 10 seconds for high pressure.
        const int PerCoreLowTrimCount = 1; // Trim 1 item when pressure is low.
        const int PerCoreMediumTrimCount = 2; // Trim 2 items when pressure is moderate.
        const int PerCoreHighTrimCount = Utils.MaxObjectsPerCore; // Trim all items when pressure is high.

        const int ThreadLocalLowMilliseconds = 30 * 1000; // Trim after 30 seconds for moderate pressure.
        const int ThreadLocalMediumMilliseconds = 15 * 1000; // Trim after 15 seconds for low pressure.

        const int ReserveLowTrimAfterMilliseconds = 90 * 1000; // Trim after 90 seconds for low pressure.
        const int ReserveMediumTrimAfterMilliseconds = 45 * 1000; // Trim after 45 seconds for low pressure.
        const float ReserveLowTrimPercentage = .10f; // Trim 10% of elements for low pressure.
        const float ReserveMediumTrimPercentage = .30f; // Trim 30% of elements for moderate pressure.

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
            SharedPerCoreStack<T>[] perCoreStacks_ = perCoreStacks;
            ref SharedPerCoreStack<T> current = ref Utils.GetArrayDataReference(perCoreStacks_);
            ref SharedPerCoreStack<T> end = ref Unsafe.Add(ref current, perCoreStacks_.Length);
            // Trim each of the per-core stacks.
            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                current.TryTrim(currentMilliseconds, perCoreTrimMilliseconds, perCoreTrimCount);
                current = ref Unsafe.Add(ref current, 1);
            }
        }

        SpinWait spinWait;
        {
            // Trim each of the thread local fields.
            // Note that threads may be modifying their thread local fields concurrently with this trimming happening.
            // We do not force synchronization with those operations, so we accept the fact
            // that we may potentially trim an object we didn't need to.
            // Both of these should be rare occurrences.

            spinWait = new();
            GCHandle[]? allThreadLocalElements_;
            while (true)
            {
                allThreadLocalElements_ = Interlocked.Exchange(ref allThreadLocalElements, null);
                if (allThreadLocalElements_ is not null)
                    break;
                spinWait.SpinOnce();
            }
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
                    Debug.Assert(target is SharedThreadLocalElement<T?>);
                    Unsafe.As<SharedThreadLocalElement<T?>>(target).Clear();
                    Debug.Assert(Unsafe.IsAddressLessThan(ref newCurrent, ref end));
                    newCurrent = handle;
#if DEBUG
                    Debug.Assert(count++ < length);
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
                    Debug.Assert(target is SharedThreadLocalElement<T?>);
                    Unsafe.As<SharedThreadLocalElement<T?>>(target).Trim(currentMilliseconds, threadLocalTrimMilliseconds);
                    Debug.Assert(Unsafe.IsAddressLessThan(ref newCurrent, ref end));
                    newCurrent = handle;
#if DEBUG
                    Debug.Assert(count++ < length);
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

        spinWait = new();
        T[]? globalReserve_;
        while (true)
        {
            globalReserve_ = Interlocked.Exchange(ref globalReserve, null);
            if (globalReserve_ is not null)
                break;
            spinWait.SpinOnce();
        }
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
                if (globalReserve_.Length <= Utils.MaxObjectsPerCore)
                {
#if NET6_0_OR_GREATER
                    Array.Clear(globalReserve_);
#else
                    Array.Clear(globalReserve_, 0, globalReserve_.Length);
#endif
                }
                else
                    globalReserve_ = new T[Utils.InitialGlobalReserveCapacity];
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
                        if (globalLength <= Utils.InitialGlobalReserveCapacity)
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
    private static SharedThreadLocalElement<T?> InitializeThreadLocalElement()
    {
        SharedThreadLocalElement<T?> slot = new();
        threadLocalElement = slot;

        SpinWait spinWait = new();
        GCHandle[]? allThreadLocalElements_;
        while (true)
        {
            allThreadLocalElements_ = Interlocked.Exchange(ref allThreadLocalElements, null);
            if (allThreadLocalElements_ is not null)
                break;
            spinWait.SpinOnce();
        };

        int count_ = allThreadLocalElementsCount;
        if (unchecked((uint)count_ >= (uint)allThreadLocalElements_.Length))
        {
            ref GCHandle current = ref Utils.GetArrayDataReference(allThreadLocalElements_);
            ref GCHandle end = ref Unsafe.Add(ref current, allThreadLocalElements_.Length);

            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                GCHandle handle = current;
                Debug.Assert(handle.IsAllocated);
                object? target = handle.Target;
                if (target is null)
                {
                    handle.Target = slot;
                    goto end;
                }
                current = ref Unsafe.Add(ref current, 1);
            }

            Array.Resize(ref allThreadLocalElements_, allThreadLocalElements_.Length * 2);
        }

        Debug.Assert(count_ < allThreadLocalElements_.Length);
        Unsafe.Add(ref Utils.GetArrayDataReference(allThreadLocalElements_), count_) = GCHandle.Alloc(slot, GCHandleType.Weak);
        allThreadLocalElementsCount = count_ + 1;

    end:
        allThreadLocalElements = allThreadLocalElements_;
        return slot;
    }
}
