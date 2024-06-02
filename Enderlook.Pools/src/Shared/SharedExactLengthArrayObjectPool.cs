using Enderlook.Pools.Free;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Enderlook.Pools;


/// <summary>
/// A fast and thread-safe array of exact size pool to store a large amount of objects.
/// </summary>
/// <typeparam name="T">Type of object to pool.</typeparam>
internal sealed class SharedExactLengthArrayObjectPool<T> : ObjectPool<T[]>
{
    // Inspired from https://source.dot.net/#System.Private.CoreLib/TlsOverPerCoreLockedStacksArrayPool.cs

    /// <summary>
    /// Length of the array.
    /// </summary>
    private readonly int Length;

    /// <summary>
    /// A per-thread element for better cache.
    /// </summary>
    [ThreadStatic]
    private static Dictionary<int, (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool)>? ThreadLocalElements;

    /// <summary>
    /// All pools classified by element.
    /// </summary>
    private static Dictionary<int, SharedExactLengthArrayObjectPool<T>>? PoolPerLength = [];

    /// <summary>
    /// All pools. 
    /// </summary>
    private static SharedExactLengthArrayObjectPool<T>[] AllPools = new SharedExactLengthArrayObjectPool<T>[16];

    /// <summary>
    /// Count of <see cref="Pools"/>.
    /// </summary>
    private static int PoolsCount;

    public static ReadOnlySpan<SharedExactLengthArrayObjectPool<T>> Pools
    {
        get
        {
#if NET5_0_OR_GREATER
            Debug.Assert(PoolsCount <= Pools.Length);
            return MemoryMarshal.CreateReadOnlySpan(ref MemoryMarshal.GetArrayDataReference(AllPools), PoolsCount);
#else
            return AllPools.AsSpan(PoolsCount);
#endif
        }
    }

    /// <summary>
    /// Used to keep tack of all thread local objects for trimming if needed.
    /// </summary>
    private GCHandle[]? AllThreadLocalElements = new GCHandle[Environment.ProcessorCount];

    /// <summary>
    /// Keep tracks of the amount of used slots in <see cref="AllThreadLocalElements"/>.
    /// </summary>
    private static int AllThreadLocalElementsCount;

    /// <summary>
    /// An array of per-core objects.<br/>
    /// The slots are lazily initialized.
    /// </summary>
    private readonly SharedPerCoreStack[] PerCoreStacks = new SharedPerCoreStack[SharedPoolHelpers.PerCoreStacksCount];

    /// <summary>
    /// A global dynamic-size reserve of elements.<br/>
    /// When all <see cref="PerCoreStacks"/> get fulls, one of them is emptied and all its objects are moved here.<br/>
    /// When all <see cref="PerCoreStacks"/> get empty, one of them is fulled with objects from this reserve.<br/>
    /// Those operations are done in a batch to reduce the amount of times this requires to be acceded.
    /// </summary>
    private ObjectWrapper[]? GlobalReserve = new ObjectWrapper[SharedPoolHelpers.MaxObjectsPerCore];

    /// <summary>
    /// Keep tracks of the amount of used slots in <see cref="GlobalReserve"/>.
    /// </summary>
    private int GlobalReserveCount;

    /// <summary>
    /// Keep record of last time <see cref="GlobalReserve"/> was trimmed;
    /// </summary>
    private int GlobalReserveMillisecondsTimeStamp;

    private SharedExactLengthArrayObjectPool(int length)
    {
        Length = length;
        for (int i = 0; i < PerCoreStacks.Length; i++)
            PerCoreStacks[i] = new SharedPerCoreStack(new ObjectWrapper[SharedPoolHelpers.MaxObjectsPerCore]);
    }

    /// <inheritdoc cref="ObjectPool{T}.ApproximateCount"/>
    public override int ApproximateCount()
    {
        int count = SharedPerCoreStack.GetApproximatedCountOf(PerCoreStacks);

        count += SharedPoolHelpers.GetGlobalReserveCount(
            ref Unsafe.As<ObjectWrapper[]?, Array?>(ref GlobalReserve),
            ref GlobalReserveCount
        );

        count += SharedPoolHelpers.GetAllThreadLocalsCountReference(
            ref AllThreadLocalElements,
            ref AllThreadLocalElementsCount
        );

        return count;
    }

    /// <inheritdoc cref="ObjectPool{T}.Rent"/>
    public override T[] Rent()
    {
        Dictionary<int, (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool)> threadLocalElements_ = ThreadLocalElements ?? new();
        int length_ = Length;

#if NET6_0_OR_GREATER
        ref (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple = ref CollectionsMarshal.GetValueRefOrAddDefault(threadLocalElements_, length_, out bool exists);
        SharedThreadLocalElementReference threadLocalElement_ = exists ? tuple.Element : InitializeThreadLocalElement(ref tuple);
#else
        SharedThreadLocalElementReference threadLocalElement_;
        if (threadLocalElements_.TryGetValue(length_, out (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple))
            threadLocalElement_ = tuple.Element;
        else
            threadLocalElement_ = InitializeThreadLocalElement(length_);
#endif
        Debug.Assert(threadLocalElements_ is not null);

        // First, try to get an element from the thread local field if possible.
        if (threadLocalElement_ is not null)
        {
            object? element = threadLocalElement_.Value;
            if (element is not null)
            {
                threadLocalElement_.Value = null;
                Debug.Assert(element is T[]);
                return Unsafe.As<T[]>(element);
            }
        }

        return RentCommonPath();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T[] Rent_(int length)
    {
        Dictionary<int, (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool)> threadLocalElements_ = ThreadLocalElements ??= new();

        int length_ = length;
#if NET6_0_OR_GREATER
        ref (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple = ref CollectionsMarshal.GetValueRefOrAddDefault(threadLocalElements_, length_, out bool exists);
        SharedThreadLocalElementReference threadLocalElement_ = exists ? tuple.Element : InitializeThreadLocalElementStatic(length_, ref tuple).Element;
#else
        SharedThreadLocalElementReference threadLocalElement_;
        if (threadLocalElements_.TryGetValue(length_, out (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple))
            threadLocalElement_ = tuple.Element;
        else
            threadLocalElement_ = InitializeThreadLocalElementStatic(length_).Element;
#endif
        Debug.Assert(threadLocalElements_ is not null);

        {
            // First, try to get an element from the thread local field if possible.
            object? element = threadLocalElement_.Value;
            if (element is not null)
            {
                threadLocalElement_.Value = null;
                Debug.Assert(element is T[]);
                return Unsafe.As<T[]>(element);
            }
        }

        Debug.Assert(tuple.Pool.Length == length);

        return tuple.Pool.RentCommonPath();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private T[] RentCommonPath()
    {
        // Next, try to get an element from one of the per-core stacks.
        SharedPerCoreStack[] perCoreStacks_ = PerCoreStacks;
        ref SharedPerCoreStack perCoreStacks_Root = ref Utils.GetArrayDataReference(perCoreStacks_);
        // Try to pop from the associated stack first.
        // If that fails, try with other stacks.
        int index = SharedPoolHelpers.GetStartingIndex();
        for (int i = 0; i < perCoreStacks_.Length; i++)
        {
            Debug.Assert(index < perCoreStacks_.Length);
            // TODO: This Unsafe.Add could be improved to avoid the under the hood multiplication (`base + offset * size` and just do `base + offset`).
            if (Unsafe.Add(ref perCoreStacks_Root, index).TryPop(out ObjectWrapper element))
            {
                Debug.Assert(element.Value is T[]);
                return Unsafe.As<T[]>(element.Value);
            }

            if (++index == perCoreStacks_.Length)
                index = 0;
        }

        // Next, try to fill a per-core stack with objects from the global reserve.
        return SlowPath();

        T[] SlowPath()
        {
            // Next, try to fill a per-core stack with objects from the global reserve.
            if (GlobalReserveCount > 0)
            {
                int index = SharedPoolHelpers.GetStartingIndex();
                if (Unsafe.Add(ref Utils.GetArrayDataReference(PerCoreStacks), index).FillFromGlobalReserve(out ObjectWrapper element, ref GlobalReserve!, ref GlobalReserveCount))
                {
                    Debug.Assert(element.Value is T[]);
                    return Unsafe.As<T[]>(element.Value);
                }
            }
            // Finally, instantiate a new object.
            return new T[Length];
        }
    }

    /// <summary>
    /// Return rented object to pool.<br/>
    /// If the pool is full, the object will be discarded.
    /// </summary>
    /// <param name="element">Object to return.</param>
    public override void Return(T[] element)
    {
        if (element is null) Utils.ThrowArgumentNullException_Element();
        if (element.Length != Length) Utils.ThrowArgumentOutOfRangeException_ArrayLength();
        Debug.Assert(element is not null);

        Dictionary<int, (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool)> threadLocalElements_ = ThreadLocalElements ?? new();
        // Store the element into the thread local field.
        // If there's already an object in it, push that object down into the per-core stacks,
        // preferring to keep the latest one in thread local field for better locality.
        int length_ = Length;
#if NET6_0_OR_GREATER
        ref (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple = ref CollectionsMarshal.GetValueRefOrAddDefault(threadLocalElements_, length_, out bool exists);
        SharedThreadLocalElementReference threadLocalElement_ = exists ? tuple.Element : InitializeThreadLocalElement(ref tuple);
#else
        SharedThreadLocalElementReference threadLocalElement_;
        if (threadLocalElements_.TryGetValue(length_, out var tuple))
            threadLocalElement_ = tuple.Element;
        else
            threadLocalElement_ = InitializeThreadLocalElement(length_);
#endif
        Debug.Assert(threadLocalElements_ is not null);

        object? previous = threadLocalElement_.Value;
        threadLocalElement_.Value = element;
        threadLocalElement_.MillisecondsTimeStamp = 0;
        if (previous is not null)
        {
            // Try to store the object from one of the per-core stacks.
            SharedPerCoreStack[] perCoreStacks_ = PerCoreStacks;
            ref SharedPerCoreStack perCoreStacks_Root = ref Utils.GetArrayDataReference(perCoreStacks_);
            // Try to push from the associated stack first.
            // If that fails, try with other stacks.
            int index = SharedPoolHelpers.GetStartingIndex();
            ObjectWrapper previous_ = new(previous);
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
            Unsafe.Add(ref perCoreStacks_Root, index).MoveToGlobalReserve(previous_, ref GlobalReserve!, ref GlobalReserveCount);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return_(T[] element)
    {
        int length = element.Length;
        Dictionary<int, (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool)> threadLocalElements_ = ThreadLocalElements ?? new();
        // Store the element into the thread local field.
        // If there's already an object in it, push that object down into the per-core stacks,
        // preferring to keep the latest one in thread local field for better locality.
#if NET6_0_OR_GREATER
        ref (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple = ref CollectionsMarshal.GetValueRefOrAddDefault(threadLocalElements_, length, out bool exits);
        SharedThreadLocalElementReference threadLocalElement_ = exits ? tuple.Element : InitializeThreadLocalElementStatic(length, ref tuple).Element;
#else
        SharedThreadLocalElementReference threadLocalElement_;
        if (threadLocalElements_.TryGetValue(length, out (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple))
            threadLocalElement_ = tuple.Element;
        else
            threadLocalElement_ = InitializeThreadLocalElementStatic(length).Element;
#endif
        Debug.Assert(threadLocalElements_ is not null);

        object? previous = threadLocalElement_.Value;
        threadLocalElement_.Value = element;
        threadLocalElement_.MillisecondsTimeStamp = 0;
        if (previous is not null)
        {
            Debug.Assert(previous is T[]);
            tuple.Pool.ReturnCommonPath(Unsafe.As<T[]>(previous));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReturnCommonPath(T[] previous)
    {
        // Try to store the object from one of the per-core stacks.
        SharedPerCoreStack[] perCoreStacks_ = PerCoreStacks;
        ref SharedPerCoreStack perCoreStacks_Root = ref Utils.GetArrayDataReference(perCoreStacks_);
        // Try to push from the associated stack first.
        // If that fails, try with other stacks.
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        int currentProcessorId = Thread.GetCurrentProcessorId();
#else
        int currentProcessorId = Thread.CurrentThread.ManagedThreadId; // TODO: This is probably a bad idea.
#endif
        int index = (int)((uint)currentProcessorId % (uint)SharedPoolHelpers.PerCoreStacksCount);
        ObjectWrapper previous_ = new(previous);
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
        Unsafe.Add(ref perCoreStacks_Root, index).MoveToGlobalReserve(previous_, ref GlobalReserve!, ref GlobalReserveCount);
    }

    /// <inheritdoc cref="ObjectPool{T}.Trim(bool)"/>
    public override void Trim(bool force = false)
    {
        SharedTrimInfo info = new(force);

        info.TryTrimThreadLocalElements<ReferenceNotDisposable>(
            ref AllThreadLocalElements,
            ref AllThreadLocalElementsCount
        );

        info.TryTrimPerCoreStacks<ReferenceNotDisposable>(PerCoreStacks);

        info.TryTrimGlobalReserve<ReferenceNotDisposable>(
            ref Unsafe.As<ObjectWrapper[]?, Array?>(ref GlobalReserve),
            ref GlobalReserveCount,
            ref GlobalReserveMillisecondsTimeStamp
        );
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SharedThreadLocalElementReference InitializeThreadLocalElement(
#if NET6_0_OR_GREATER
        ref (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple
#else
        int length
#endif
        )
    {
        SharedThreadLocalElementReference slot = new();

#if NET6_0_OR_GREATER
        tuple.Element = slot;
        tuple.Pool = this;
#else
        Dictionary<int, (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool)>? threadLocalElements_ = ThreadLocalElements;
        Debug.Assert(threadLocalElements_ is not null);
        threadLocalElements_.Add(length, (slot, this));
#endif

        GCHandle[]? allThreadLocalElements_ = Utils.NullExchange(ref AllThreadLocalElements);

        int count_ = AllThreadLocalElementsCount;
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
        AllThreadLocalElementsCount = count_ + 1;

    end:
        AllThreadLocalElements = allThreadLocalElements_;
        return slot;
    }

    public static SharedExactLengthArrayObjectPool<T> GetPool(int length)
    {
        Dictionary<int, (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool)> threadLocalElements_ = ThreadLocalElements ?? new();
#if NET6_0_OR_GREATER
        ref (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple = ref CollectionsMarshal.GetValueRefOrAddDefault(threadLocalElements_, length, out bool exists);
        if (exists)
            return tuple.Pool;
        else
            return InitializeThreadLocalElementStatic(length, ref tuple).Pool;
#else
        if (threadLocalElements_.TryGetValue(length, out (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple))
            return tuple.Pool;
        else
            return InitializeThreadLocalElementStatic(length).Pool;
#endif
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) InitializeThreadLocalElementStatic(int length
#if NET6_0_OR_GREATER
        , ref (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple
#endif
        )
    {
        SharedExactLengthArrayObjectPool<T> pool = GetOrCreateGlobalPool(length);
        SharedThreadLocalElementReference threadLocalElement = pool.InitializeThreadLocalElement(
#if NET6_0_OR_GREATER
            ref tuple
#else
            length
#endif
        );
        return (threadLocalElement, pool);
    }

    private static SharedExactLengthArrayObjectPool<T> GetOrCreateGlobalPool(int length)
    {
        SpinWait spinWait = new();
        while (true)
        {
            Dictionary<int, SharedExactLengthArrayObjectPool<T>>? pools_ = Interlocked.Exchange(ref PoolPerLength, null);
            if (pools_ is null)
                spinWait.SpinOnce();
            else
            {
                SharedExactLengthArrayObjectPool<T>? pool_;
#if NET6_0_OR_GREATER
                ref SharedExactLengthArrayObjectPool<T>? pool = ref CollectionsMarshal.GetValueRefOrAddDefault(pools_, length, out bool exists);
                if (!exists)
                {
                    pool_ = pool = new(length);
#else
                if (!pools_.TryGetValue(length, out pool_))
                {
                    pool_ = new(length);
                    pools_.Add(length, pool_);
#endif

                    SharedExactLengthArrayObjectPool<T>[] array = AllPools;
                    int count = PoolsCount;
                    if (unchecked((uint)count <= (uint)array.Length))
                        array[count] = pool_;
                    else
                    {
                        Array.Resize(ref array, array.Length * 2);
                        array[count] = pool_;
                        AllPools = array;
                    }
                    // We must to modify count after setting the array, so if there is a race condition,
                    // other parts of the code doesn't get an out of range exception.
                    PoolsCount = count + 1;
                    Debug.Assert(pools_.Count == PoolsCount);
                }
#if NET6_0_OR_GREATER
                else
                {
                    Debug.Assert(pool is not null);
                    pool_ = pool;
                }
#endif

                PoolPerLength = pools_;
                return pool_;
            }
        }
    }
}