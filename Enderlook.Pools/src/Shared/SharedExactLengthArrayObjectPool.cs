﻿using Enderlook.Pools.Free;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Enderlook.Pools;

/// <summary>
/// A fast and thread-safe array of exact size pool to store a large amount of objects.
/// </summary>
/// <typeparam name="T">Type of object to pool.</typeparam>
internal sealed class SharedExactLengthArrayObjectPool<T> : ObjectPool<T[]>
{
    /// <summary>
    /// Length of the array.
    /// </summary>
    private readonly int Length;

    /// <summary>
    /// A per-thread element for better cache.
    /// </summary>
    [ThreadStatic]
    private static Dictionary<int, (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool)>? ThreadLocals;

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

    /// <summary>
    /// Span of all registered pools.
    /// </summary>
    public static ReadOnlySpan<SharedExactLengthArrayObjectPool<T>> Pools
    {
        get
        {
#if NET5_0_OR_GREATER
            Debug.Assert(PoolsCount <= AllPools.Length, "Index out of range.");
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
    private int AllThreadLocalElementsCount;

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
        SharedPerCoreStack[] perCoreStacks = PerCoreStacks;
        for (int i = 0; i < perCoreStacks.Length; i++)
            perCoreStacks[i] = new SharedPerCoreStack(new ObjectWrapper[SharedPoolHelpers.MaxObjectsPerCore]);
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
        Dictionary<int, (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool)> threadLocals = ThreadLocals ?? [];
        int length = Length;

#if NET6_0_OR_GREATER
        ref (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple = ref CollectionsMarshal.GetValueRefOrAddDefault(threadLocals, length, out bool exists);
        SharedThreadLocalElementReference threadLocalElement = exists ? tuple.Element : InitializeThreadLocalElement(ref tuple);
#else
        SharedThreadLocalElementReference threadLocalElement;
        if (threadLocals.TryGetValue(length, out (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple))
            threadLocalElement = tuple.Element;
        else
            threadLocalElement = InitializeThreadLocalElement(length);
#endif
        Debug.Assert(threadLocals is not null);

        // First, try to get an element from the thread local field if possible.
        if (threadLocalElement is not null)
        {
            object? element = threadLocalElement.Value;
            if (element is not null)
            {
                threadLocalElement.Value = null;
                Debug.Assert(element is T[]);
                return Unsafe.As<T[]>(element);
            }
        }

        return RentCommonPath();
    }

    public static T[] Rent_(int length)
    {
        Dictionary<int, (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool)> threadLocals = ThreadLocals ??= new();

#if NET6_0_OR_GREATER
        ref (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple = ref CollectionsMarshal.GetValueRefOrAddDefault(threadLocals, length, out bool exists);
        SharedThreadLocalElementReference threadLocalElement = exists ? tuple.Element : InitializeThreadLocalElementStatic(length, ref tuple).Element;
#else
        SharedThreadLocalElementReference threadLocalElement;
        if (threadLocals.TryGetValue(length, out (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple))
            threadLocalElement = tuple.Element;
        else
            threadLocalElement = InitializeThreadLocalElementStatic(length).Element;
#endif
        Debug.Assert(threadLocals is not null);

        {
            // First, try to get an element from the thread local field if possible.
            object? element = threadLocalElement.Value;
            if (element is not null)
            {
                threadLocalElement.Value = null;
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
        SharedPerCoreStack[] perCoreStacks = PerCoreStacks;
        ref SharedPerCoreStack perCoreStacksRoot = ref Utils.GetArrayDataReference(perCoreStacks);
        // Try to pop from the associated stack first.
        // If that fails, try with other stacks.
        int index = SharedPoolHelpers.GetStartingIndex();
        bool failedAttempt = false;
        for (int i = 0; i < perCoreStacks.Length; i++)
        {
            Debug.Assert(index < perCoreStacks.Length);
            // TODO: This Unsafe.Add could be improved to avoid the under the hood multiplication (`base + offset * size` and just do `base + offset`).
            int value = Unsafe.Add(ref perCoreStacksRoot, index).TryPop(out ObjectWrapper element2, failedAttempt);
            if (value > 0)
            {
                Debug.Assert(element2.Value is T[]);
                T[]? element3 = Unsafe.As<object?, T[]?>(ref element2.Value);
                Debug.Assert(element3 is not null);
                return element3;
            }
            failedAttempt |= value == -1;

            if (++index == perCoreStacks.Length)
                index = 0;
        }

        if (failedAttempt)
            return SlowPathForce();

        // Next, try to fill a per-core stack with objects from the global reserve.
        return SlowPath();

        T[] SlowPathForce()
        {
            // Next, try to get an element from one of the per-core stacks.
            SharedPerCoreStack[] perCoreStacks = PerCoreStacks;
            ref SharedPerCoreStack perCoreStacksRoot = ref Utils.GetArrayDataReference(perCoreStacks);
            // Try to pop from the associated stack first.
            // If that fails, try with other stacks.
            int index = SharedPoolHelpers.GetStartingIndex();
            for (int i = 0; i < perCoreStacks.Length; i++)
            {
                Debug.Assert(index < perCoreStacks.Length);
                // TODO: This Unsafe.Add could be improved to avoid the under the hood multiplication (`base + offset * size` and just do `base + offset`).
                if (Unsafe.Add(ref perCoreStacksRoot, index).TryPop(out ObjectWrapper element2, true) > 0)
                {
                    Debug.Assert(element2.Value is T[]);
                    T[]? element3 = Unsafe.As<object?, T[]?>(ref element2.Value);
                    Debug.Assert(element3 is not null);
                    return element3;
                }

                if (++index == perCoreStacks.Length)
                    index = 0;
            }

            // Next, try to fill a per-core stack with objects from the global reserve.
            return SlowPath();
        }

        T[] SlowPath()
        {
            // Next, try to fill a per-core stack with objects from the global reserve.
            if (GlobalReserveCount > 0)
            {
                int index = SharedPoolHelpers.GetStartingIndex();

                if (Unsafe.Add(ref Utils.GetArrayDataReference(PerCoreStacks), index).
                    FillFromGlobalReserve(out ObjectWrapper element, ref GlobalReserve!, ref GlobalReserveCount))
                {
                    Debug.Assert(element.Value is T[]);
                    return Unsafe.As<object?, T[]>(ref element.Value!);
                }
            }
            // Finally, instantiate a new object.
            int length = Length;
            if (length == 0)
                return Array.Empty<T>();
#if NET5_0_OR_GREATER
            return GC.AllocateUninitializedArray<T>(length);
#else
            return new T[length];
#endif
        }
    }

    /// <inheritdoc cref="ObjectPool{T}.Return(T)"/>
    public override void Return(T[] element)
    {
        if (element is null) Utils.ThrowArgumentNullException_Element();
        int length = Length;
        if (element.Length != length) Utils.ThrowArgumentOutOfRangeException_ArrayLength();
        if (length == 0) return;

        Dictionary<int, (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool)> threadLocals = ThreadLocals ?? [];
        // Store the element into the thread local field.
        // If there's already an object in it, push that object down into the per-core stacks,
        // preferring to keep the latest one in thread local field for better locality.
#if NET6_0_OR_GREATER
        ref (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple = ref CollectionsMarshal.GetValueRefOrAddDefault(threadLocals, length, out bool exists);
        SharedThreadLocalElementReference threadLocalElement = exists ? tuple.Element : InitializeThreadLocalElement(ref tuple);
#else
        SharedThreadLocalElementReference threadLocalElement;
        if (threadLocals.TryGetValue(length, out var tuple))
            threadLocalElement = tuple.Element;
        else
            threadLocalElement = InitializeThreadLocalElement(length);
#endif
        Debug.Assert(threadLocals is not null);

        object? old = threadLocalElement.Value;
        threadLocalElement.Value = element;
        threadLocalElement.MillisecondsTimeStamp = 0;
        if (old is not null)
        {
            Debug.Assert(old is T[]);
            tuple.Pool.ReturnCommonPath(Unsafe.As<T[]>(old));
        }
    }

    public static void Return_(T[] element)
    {
        int length = element.Length;
        if (length == 0) return;
        Dictionary<int, (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool)> threadLocals = ThreadLocals ?? [];
        // Store the element into the thread local field.
        // If there's already an object in it, push that object down into the per-core stacks,
        // preferring to keep the latest one in thread local field for better locality.
#if NET6_0_OR_GREATER
        ref (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple = ref CollectionsMarshal.GetValueRefOrAddDefault(threadLocals, length, out bool exits);
        SharedThreadLocalElementReference threadLocalElement = exits ? tuple.Element : InitializeThreadLocalElementStatic(length, ref tuple).Element;
#else
        SharedThreadLocalElementReference threadLocalElement;
        if (threadLocals.TryGetValue(length, out (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple))
            threadLocalElement = tuple.Element;
        else
            threadLocalElement = InitializeThreadLocalElementStatic(length).Element;
#endif
        Debug.Assert(threadLocals is not null);

        object? old = threadLocalElement.Value;
        threadLocalElement.Value = element;
        threadLocalElement.MillisecondsTimeStamp = 0;
        if (old is not null)
        {
            Debug.Assert(old is T[]);
            tuple.Pool.ReturnCommonPath(Unsafe.As<T[]>(old));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReturnCommonPath(T[] old)
    {
        // Try to store the object from one of the per-core stacks.
        // We don't use a helper method, because calling it (even with aggressive inlining) produced an additional branching.
        SharedPerCoreStack[] perCoreStacks = PerCoreStacks;
        ref SharedPerCoreStack perCoreStacks_Root = ref Utils.GetArrayDataReference(perCoreStacks);
        // Try to push from the associated stack first.
        // If that fails, try with other stacks.
        int index = SharedPoolHelpers.GetStartingIndex();
        for (int i = 0; i < perCoreStacks.Length; i++)
        {
            Debug.Assert(index < perCoreStacks.Length);
            if (Unsafe.Add(ref perCoreStacks_Root, index).TryPush(new ObjectWrapper(old)))
                return;

            if (++index == perCoreStacks.Length)
                index = 0;
        }

        SlowPath(index, old);

        [MethodImpl(MethodImplOptions.NoInlining)]
        void SlowPath(int index, object element)
        {
            SharedPerCoreStack[] perCoreStacks = PerCoreStacks;
            ref SharedPerCoreStack perCoreStacksRoot = ref Utils.GetArrayDataReference(perCoreStacks);
            // Next, transfer a per-core stack to the global reserve.
            Debug.Assert(index < perCoreStacks.Length);
            Unsafe.Add(ref perCoreStacksRoot, index).MoveToGlobalReserve(
                new ObjectWrapper(element),
                ref GlobalReserve,
                ref GlobalReserveCount
            );
        }
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

    public static SharedExactLengthArrayObjectPool<T> GetPool(int length)
    {
        Dictionary<int, (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool)> threadLocals = ThreadLocals ?? new();
#if NET6_0_OR_GREATER
        ref (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple = ref CollectionsMarshal.GetValueRefOrAddDefault(threadLocals, length, out bool exists);
        return exists ? tuple.Pool : InitializeThreadLocalElementStatic(length, ref tuple).Pool;
#else
        return threadLocals.TryGetValue(length, out (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool) tuple)
            ? tuple.Pool
            : InitializeThreadLocalElementStatic(length).Pool;
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
        Dictionary<int, (SharedThreadLocalElementReference Element, SharedExactLengthArrayObjectPool<T> Pool)>? threadLocals = ThreadLocals;
        Debug.Assert(threadLocals is not null);
        threadLocals.Add(length, (slot, this));
#endif

        GCHandle[]? allThreadLocalElements = Utils.NullExchange(ref AllThreadLocalElements);

        int count = AllThreadLocalElementsCount;
        if (unchecked((uint)count >= (uint)allThreadLocalElements.Length))
        {
            ref GCHandle current = ref Utils.GetArrayDataReference(allThreadLocalElements);
            ref GCHandle end = ref Unsafe.Add(ref current, allThreadLocalElements.Length);

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

            Array.Resize(ref allThreadLocalElements, allThreadLocalElements.Length * 2);
        }

        Debug.Assert(count < allThreadLocalElements.Length);
        Unsafe.Add(ref Utils.GetArrayDataReference(allThreadLocalElements), count) = GCHandle.Alloc(slot, GCHandleType.Weak);
        AllThreadLocalElementsCount = count + 1;

    end:
        AllThreadLocalElements = allThreadLocalElements;
        return slot;
    }

    private static SharedExactLengthArrayObjectPool<T> GetOrCreateGlobalPool(int length)
    {
        Dictionary<int, SharedExactLengthArrayObjectPool<T>>? pools = Utils.NullExchange(ref PoolPerLength);
        SharedExactLengthArrayObjectPool<T>? pool;
#if NET6_0_OR_GREATER
        ref SharedExactLengthArrayObjectPool<T>? poolSlot = ref CollectionsMarshal.GetValueRefOrAddDefault(pools, length, out bool exists);
        if (!exists)
        {
            pool = poolSlot = new(length);
#else
        if (!pools.TryGetValue(length, out pool))
        {
            pool = new(length);
            pools.Add(length, pool);
#endif

            SharedExactLengthArrayObjectPool<T>[] array = AllPools;
            int count = PoolsCount;
            if (unchecked((uint)count >= (uint)array.Length))
            {
                Array.Resize(ref array, array.Length * 2);
                AllPools = array;
            }
            array[count] = pool;
            // We must to modify count after setting the array, so if there is a race condition,
            // other parts of the code doesn't get an out of range exception.
            PoolsCount = count + 1;
            Debug.Assert(pools.Count == PoolsCount);
        }
#if NET6_0_OR_GREATER
        else
        {
            Debug.Assert(poolSlot is not null);
            pool = poolSlot;
        }
#endif

        PoolPerLength = pools;
        return pool;
    }
}