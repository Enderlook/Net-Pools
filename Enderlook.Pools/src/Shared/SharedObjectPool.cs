using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Enderlook.Pools;

/// <summary>
/// A fast and thread-safe object pool to store a large amount of objects.
/// </summary>
/// <typeparam name="TElement">Type of object to pool.</typeparam>
/// <typeparam name="TLocal">Type for thread-local elements.</typeparam>
/// <typeparam name="TStorage">Type for multithreading elements.</typeparam>
internal sealed class SharedObjectPool<
#if NET5_0_OR_GREATER
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
#endif
TElement, TLocal, TStorage> : ObjectPool<TElement>
{
    // Inspired from https://source.dot.net/#System.Private.CoreLib/TlsOverPerCoreLockedStacksArrayPool.cs

    /// <summary>
    /// A per-thread element for better cache.
    /// </summary>
    [ThreadStatic]
    private static TLocal? threadLocalElement;

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
    private static readonly SharedPerCoreStack<TStorage>[] perCoreStacks = new SharedPerCoreStack<TStorage>[Utils.PerCoreStacksCount];

    /// <summary>
    /// A global dynamic-size reserve of elements.<br/>
    /// When all <see cref="perCoreStacks"/> get fulls, one of them is emptied and all its objects are moved here.<br/>
    /// When all <see cref="perCoreStacks"/> get empty, one of them is fulled with objects from this reserve.<br/>
    /// Those operations are done in a batch to reduce the amount of times this requires to be acceded.
    /// </summary>
    private static TStorage[]? globalReserve = new TStorage[Utils.MaxObjectsPerCore];

    /// <summary>
    /// Keep tracks of the amount of used slots in <see cref="globalReserve"/>.
    /// </summary>
    private static int globalReserveCount;

    /// <summary>
    /// Keep record of last time <see cref="globalReserve"/> was trimmed;
    /// </summary>
    private static int globalReserveMillisecondsTimeStamp;

    static SharedObjectPool()
    {
        for (int i = 0; i < perCoreStacks.Length; i++)
            perCoreStacks[i] = new SharedPerCoreStack<TStorage>(new TStorage[Utils.MaxObjectsPerCore]);

#if DEBUG
        if (typeof(TElement).IsValueType)
        {
            Debug.Assert(typeof(TElement) == typeof(TStorage));
#if NET5_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TElement>())
#endif
                // Required so during trim we can atomically remove elements.
                // Internally the object stored is of type `NullableS<TElement>`.
                Debug.Assert(typeof(TLocal) == typeof(SharedThreadLocalElement));
#if NET5_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            else
                Debug.Assert(typeof(TLocal) == typeof(NullableS<TElement>));
#endif
        }
        else
        {
            Debug.Assert(typeof(TStorage) == typeof(ObjectWrapper));
            Debug.Assert(typeof(TLocal) == typeof(SharedThreadLocalElement));
        }
#endif
    }

    /// <inheritdoc cref="ObjectPool{T}.ApproximateCount"/>
    public override int ApproximateCount()
    {
        SharedPerCoreStack<TStorage>[] perCoreStacks_ = perCoreStacks;
        ref SharedPerCoreStack<TStorage> current = ref Utils.GetArrayDataReference(perCoreStacks_);
        ref SharedPerCoreStack<TStorage> end = ref Unsafe.Add(ref current, perCoreStacks_.Length);
        int count = 0;
        while (Unsafe.IsAddressLessThan(ref current, ref end))
        {
            count += current.GetCount();
            current = ref Unsafe.Add(ref current, 1);
        }

        SpinWait spinWait = new();
        TStorage[]? globalReserve_;
        while (true)
        {
            globalReserve_ = Interlocked.Exchange(ref globalReserve, null);
            if (globalReserve_ is not null)
                break;
            spinWait.SpinOnce();
        }
        count += globalReserveCount;
        globalReserve = globalReserve_;

        spinWait = new();
        GCHandle[]? allThreadLocalElements_;
        while (true)
        {
            allThreadLocalElements_ = Interlocked.Exchange(ref allThreadLocalElements, null);
            if (allThreadLocalElements_ is not null)
                break;
            spinWait.SpinOnce();
        }

        ref GCHandle current2 = ref Utils.GetArrayDataReference(allThreadLocalElements_);
        ref GCHandle end2 = ref Unsafe.Add(ref current2, allThreadLocalElementsCount);
        while (Unsafe.IsAddressLessThan(ref current2, ref end2))
        {
            SharedThreadLocalElement? sharedThreadLocalElement = Unsafe.As<SharedThreadLocalElement?>(current2.Target);
            if (sharedThreadLocalElement is not null)
            {
                object? value = sharedThreadLocalElement.Value;
                if (typeof(TElement).IsValueType)
                {
                    Debug.Assert(value is null or NullableC<TElement>);
                    if (value is not null && Unsafe.As<NullableC<TElement>>(value).Has)
                        count++;
                }
                else
                {
                    Debug.Assert(value is null or TElement);
                    if (value is not null)
                        count++;
                }
            }
            current2 = Unsafe.Add(ref current2, 1);
        }

        allThreadLocalElements = allThreadLocalElements_;

        return count;
    }

    /// <inheritdoc cref="ObjectPool{T}.Rent"/>
    public override TElement Rent()
    {
        // First, try to get an element from the thread local field if possible.
        if (typeof(TElement).IsValueType)
        {
#if NET5_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TLocal>())
#endif
            {
                TLocal? threadLocalElement_ = threadLocalElement;
                if (threadLocalElement_ is not null)
                {
                    SharedThreadLocalElement threadLocalElement_2 = Unsafe.As<TLocal, SharedThreadLocalElement>(ref threadLocalElement_);
                    object? element = threadLocalElement_2.Value;
                    if (element is not null)
                    {
                        Debug.Assert(element is null or NullableC<TElement>);
                        threadLocalElement_2.Value = default;
                        NullableC<TElement>? element_ = Unsafe.As<NullableC<TElement>?>(element);
                        Debug.Assert(element_ is not null);
                        if (element_.Has)
                        {
                            element_.Has = false;
                            TElement? result = element_.Value;
                            Debug.Assert(result is not null);
                            element_.Value = default;
                            return result;
                        }
                    }
                }
            }
#if NET5_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            else
            {
                ref NullableS<TElement> threadLocalElement_ = ref Unsafe.As<TLocal?, NullableS<TElement>>(ref threadLocalElement);
                if (threadLocalElement_.Has)
                {
                    threadLocalElement_.Has = false;
                    TElement? result = threadLocalElement_.Value;
                    Debug.Assert(result is not null);
                    threadLocalElement_.Value = default;
                    return result;
                }
            }
#endif
        }
        else
        {
            TLocal? threadLocalElement_ = threadLocalElement;
            if (threadLocalElement_ is not null)
            {
                SharedThreadLocalElement threadLocalElement_2 = Unsafe.As<TLocal, SharedThreadLocalElement>(ref threadLocalElement_);
                object? element = threadLocalElement_2.Value;
                if (element is not null)
                {
                    Debug.Assert(element is null or TElement);
                    threadLocalElement_2.Value = default;
                    return Unsafe.As<object?, TElement>(ref element);
                }
            }
        }

        // Next, try to get an element from one of the per-core stacks.
        SharedPerCoreStack<TStorage>[] perCoreStacks_ = perCoreStacks;
        ref SharedPerCoreStack<TStorage> perCoreStacks_Root = ref Utils.GetArrayDataReference(perCoreStacks_);
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
            if (Unsafe.Add(ref perCoreStacks_Root, index).TryPop(out TStorage element))
            {
                if (typeof(TElement).IsValueType)
                    return Unsafe.As<TStorage, TElement>(ref element);
                else
                {
                    TElement? element_ = Unsafe.As<object?, TElement?>(ref Unsafe.As<TStorage, ObjectWrapper>(ref element).Value);
                    Debug.Assert(element_ is not null);
                    return element_;
                }
            }

            if (++index == perCoreStacks_.Length)
                index = 0;
        }

        // Next, try to fill a per-core stack with objects from the global reserve.
        if (globalReserveCount > 0)
            return FillFromGlobalReserve();

        // Finally, instantiate a new object.
        return ObjectPoolHelper<TElement>.Create();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static TElement FillFromGlobalReserve()
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            int currentProcessorId = Thread.GetCurrentProcessorId();
#else
            int currentProcessorId = Thread.CurrentThread.ManagedThreadId; // TODO: This is probably a bad idea.
#endif
            int index = (int)((uint)currentProcessorId % (uint)Utils.PerCoreStacksCount);

            if (Unsafe.Add(ref Utils.GetArrayDataReference(perCoreStacks), index).FillFromGlobalReserve(out TStorage? element, ref globalReserve!, ref globalReserveCount))
            {
                if (typeof(TElement).IsValueType)
                    return Unsafe.As<TStorage?, TElement>(ref element);
                else
                {
                    TElement? element_ = Unsafe.As<object?, TElement?>(ref Unsafe.As<TStorage?, ObjectWrapper>(ref element).Value);
                    Debug.Assert(element_ is not null);
                    return element_;
                }
            }
            // Finally, instantiate a new object.
            return ObjectPoolHelper<TElement>.Create();
        }
    }

    /// <summary>
    /// Return rented object to pool.<br/>
    /// If the pool is full, the object will be discarded.
    /// </summary>
    /// <param name="element">Object to return.</param>
    public override void Return(TElement element)
    {
        if (element is null) Utils.ThrowArgumentNullException_Element();
        Debug.Assert(element is not null);

        // Store the element into the thread local field.
        // If there's already an object in it, push that object down into the per-core stacks,
        // preferring to keep the latest one in thread local field for better locality.
        TElement? previous;
        bool hasPrevious;
        if (typeof(TElement).IsValueType)
        {
#if NET5_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TElement>())
#endif
            {
                TLocal? local = threadLocalElement ?? InitializeThreadLocalElement();
                SharedThreadLocalElement? threadLocalElement_ = Unsafe.As<TLocal?, SharedThreadLocalElement?>(ref local);
                Debug.Assert(threadLocalElement_ is not null);
                NullableC<TElement>? slot = Unsafe.As<NullableC<TElement>?>(threadLocalElement_.Value);
                threadLocalElement_.MillisecondsTimeStamp = 0;
                if (slot is null)
                {
                    slot = new()
                    {
                        Has = true,
                        Value = element
                    };
                    threadLocalElement_.Value = slot;
                }
                else if (slot.Has)
                {
                    previous = slot.Value;
                    slot.Value = element;
                    hasPrevious = true;
                    goto check;
                }
                slot.Has = true;
                slot.Value = element;
                hasPrevious = false;
#if NET5_0_OR_GREATER
                Unsafe.SkipInit(out previous);
#else
                previous = default;
#endif
            }
#if NET5_0_OR_GREATER
            else
            {
                ref NullableS<TElement> slot = ref Unsafe.As<TLocal?, NullableS<TElement>>(ref threadLocalElement);
                if (slot.Has)
                {
                    previous = slot.Value;
                    slot.Value = element;
                    hasPrevious = true;
                }
                else
                {
                    slot.Has = true;
                    slot.Value = element;
                    hasPrevious = false;
#if NET5_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
                    Unsafe.SkipInit(out previous);
#else
                    previous = default;
#endif
                }
            }
#elif NETSTANDARD2_1_OR_GREATER
            else
            {
                previous = default;
                hasPrevious = default;
            }
#endif
        }
        else
        {
            TLocal? local = threadLocalElement ?? InitializeThreadLocalElement();
            SharedThreadLocalElement? threadLocalElement_ = Unsafe.As<TLocal?, SharedThreadLocalElement?>(ref local);
            Debug.Assert(threadLocalElement_ is not null);
            TElement? slot = Unsafe.As<object?, TElement>(ref threadLocalElement_.Value);
            previous = slot;
            slot = element;
            threadLocalElement_.MillisecondsTimeStamp = 0;
#if NET5_0_OR_GREATER
            Unsafe.SkipInit(out previous);
            Unsafe.SkipInit(out hasPrevious);
#else
            previous = default;
            hasPrevious = default;
#endif
        }

    check:
        if (typeof(TElement).IsValueType ? hasPrevious : previous is not null)
        {
            // Try to store the object from one of the per-core stacks.
            SharedPerCoreStack<TStorage>[] perCoreStacks_ = perCoreStacks;
            ref SharedPerCoreStack<TStorage> perCoreStacks_Root = ref Utils.GetArrayDataReference(perCoreStacks_);
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
                if (Unsafe.Add(ref perCoreStacks_Root, index).TryPush(Unsafe.As<TElement?, TStorage>(ref previous)))
                    return;

                if (++index == perCoreStacks_.Length)
                    index = 0;
            }

            // Next, transfer a per-core stack to the global reserve.
            Debug.Assert(index < perCoreStacks_.Length);
            Unsafe.Add(ref perCoreStacks_Root, index).MoveToGlobalReserve(Unsafe.As<TElement?, TStorage>(ref previous), ref globalReserve!, ref globalReserveCount);
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
            SharedPerCoreStack<TStorage>[] perCoreStacks_ = perCoreStacks;
            ref SharedPerCoreStack<TStorage> current = ref Utils.GetArrayDataReference(perCoreStacks_);
            ref SharedPerCoreStack<TStorage> end = ref Unsafe.Add(ref current, perCoreStacks_.Length);
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

            GCHandle[] allThreadLocalElements_ = Utils.NullExchange(ref allThreadLocalElements);
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
                    Debug.Assert(target is SharedThreadLocalElement);
                    Unsafe.As<SharedThreadLocalElement>(target).Clear();
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
                    Debug.Assert(target is SharedThreadLocalElement);
                    Unsafe.As<SharedThreadLocalElement>(target).Trim(currentMilliseconds, threadLocalTrimMilliseconds);
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
        TStorage[]? globalReserve_;
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
                    globalReserve_ = new TStorage[Utils.InitialGlobalReserveCapacity];
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
                            TStorage[] array = new TStorage[newLength];
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
    private static TLocal InitializeThreadLocalElement()
    {
        SharedThreadLocalElement slot = new();
        TLocal? local = Unsafe.As<SharedThreadLocalElement, TLocal>(ref slot);
        SharedObjectPool<TElement, TLocal, TStorage>.threadLocalElement = local;

        GCHandle[] allThreadLocalElements_ = Utils.NullExchange(ref allThreadLocalElements);

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
        return local;
    }
}