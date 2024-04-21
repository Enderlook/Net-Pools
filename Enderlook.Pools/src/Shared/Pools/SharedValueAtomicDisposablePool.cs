#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
using Enderlook.Pools.Free;

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Enderlook.Pools;

internal sealed class SharedValueAtomicDisposablePool<TElement> : ObjectPool<TElement>
{
    /// <inheritdoc cref="ObjectPool{T}.ApproximateCount"/>
    public override int ApproximateCount()
    {
        int count = SharedPerCoreStack.GetApproximatedCountOf(SharedPool<TElement, TElement>.PerCoreStacks);

        count += SharedPoolHelpers.GetGlobalReserveCount(
            ref Unsafe.As<TElement?[]?, Array?>(ref SharedPool<TElement, TElement>.GlobalReserve),
            ref SharedPool<TElement, TElement>.GlobalReserveCount
        );

        GCHandle[]? array_ = Unsafe.As<GCHandle[]>(Utils.NullExchange(ref SharedPool<TElement, TElement>.AllThreadLocalElements));
        int count_ = 0;
        ref GCHandle current = ref Utils.GetArrayDataReference(array_);
        ref GCHandle end2 = ref Unsafe.Add(ref current, SharedPool<TElement, TElement>.AllThreadLocalElementsCount);
        while (Unsafe.IsAddressLessThan(ref current, ref end2))
        {
            SharedThreadLocalElementDisposableAtomic<TElement>? sharedThreadLocalElement = Unsafe.As<SharedThreadLocalElementDisposableAtomic<TElement>?>(current.Target);
            if (sharedThreadLocalElement is not null && sharedThreadLocalElement.Value.Has)
                count_++;
            current = Unsafe.Add(ref current, 1);
        }
        SharedPool<TElement, TElement>.AllThreadLocalElements = array_;

        return count;
    }

    /// <inheritdoc cref="ObjectPool{T}.Rent"/>
    public override TElement Rent()
    {
        SharedThreadLocalElement? threadLocalElement = SharedPool<TElement, TElement>.ThreadLocalElement;
        if (threadLocalElement is not null)
        {
            Debug.Assert(threadLocalElement is SharedThreadLocalElementDisposableAtomic<TElement>);
            SharedThreadLocalElementDisposableAtomic<TElement> threadLocalElement_ = Unsafe.As<SharedThreadLocalElementDisposableAtomic<TElement>>(threadLocalElement);
            if (threadLocalElement_.Value.Has)
            {
                long slot = Interlocked.Exchange(ref Unsafe.As<ValueAtom<TElement>, long>(ref threadLocalElement_.Value), default);
                ValueAtom<TElement> nullable = Unsafe.As<long, ValueAtom<TElement>>(ref slot);
                if (nullable.Has)
                    return nullable.Value!;
            }
        }

        // Next, try to get an element from one of the per-core stacks.
        SharedPerCoreStack[] perCoreStacks = SharedPool<TElement, TElement>.PerCoreStacks;
        ref SharedPerCoreStack perCoreStacksRoot = ref Utils.GetArrayDataReference(perCoreStacks);
        // Try to pop from the associated stack first.
        // If that fails, try with other stacks.
        int index = SharedPoolHelpers.GetStartingIndex();
        for (int i = 0; i < perCoreStacks.Length; i++)
        {
            Debug.Assert(index < perCoreStacks.Length);
            // TODO: This Unsafe.Add could be improved to avoid the under the hood multiplication (`base + offset * size` and just do `base + offset`).
            if (Unsafe.Add(ref perCoreStacksRoot, index).TryPop(out TElement element2))
                return element2;

            if (++index == perCoreStacks.Length)
                index = 0;
        }

        // Next, try to fill a per-core stack with objects from the global reserve.
        return SlowPath();

        static TElement SlowPath()
        {
            if (SharedPool<TElement, TElement>.GlobalReserveCount > 0)
            {
                int index = SharedPoolHelpers.GetStartingIndex();

                if (Unsafe.Add(
                    ref Utils.GetArrayDataReference(SharedPool<TElement, TElement>.PerCoreStacks),
                    index).FillFromGlobalReserve(
                    out TElement? element,
                    ref SharedPool<TElement, TElement>.GlobalReserve!,
                    ref SharedPool<TElement, TElement>.GlobalReserveCount))
                    return element!;
            }

            // Finally, instantiate a new object.
            return ObjectPoolHelper<TElement>.Create();
        }
    }

    /// <inheritdoc cref="ObjectPool{T}.Return(T)"/>
    public override void Return(TElement element)
    {
        if (element is null) Utils.ThrowArgumentNullException_Element();
        Debug.Assert(element is not null);

        // Store the element into the thread local field.
        // If there's already an object in it, push that object down into the per-core stacks,
        // preferring to keep the latest one in thread local field for better locality.
        object? threadLocalElement = SharedPool<TElement, ObjectWrapper>.ThreadLocalElement;
        Debug.Assert(threadLocalElement is null or SharedThreadLocalElementDisposableAtomic<TElement>);
        SharedThreadLocalElementDisposableAtomic<TElement> threadLocalElement_ = Unsafe.As<SharedThreadLocalElementDisposableAtomic<TElement>?>(threadLocalElement) ?? SlowPath();
        if (threadLocalElement_.Value.Has)
        {
            ValueAtom<TElement> nullable = new(element);
            long slot = Interlocked.Exchange(ref Unsafe.As<ValueAtom<TElement>, long>(ref threadLocalElement_.Value), Unsafe.As<ValueAtom<TElement>, long>(ref nullable));
            nullable = Unsafe.As<long, ValueAtom<TElement>>(ref slot);
            if (!nullable.Has)
                return;
            element = nullable.Value!;
        }

        // Try to store the object from one of the per-core stacks.
        // We don't use a helper method, because calling it (even with aggressive inlining) produced an additional branching.
        SharedPerCoreStack[] perCoreStacks_ = SharedPool<TElement, TElement>.PerCoreStacks;
        ref SharedPerCoreStack perCoreStacks_Root = ref Utils.GetArrayDataReference(perCoreStacks_);
        // Try to push from the associated stack first.
        // If that fails, try with other stacks.
        int index = SharedPoolHelpers.GetStartingIndex();
        for (int i = 0; i < perCoreStacks_.Length; i++)
        {
            Debug.Assert(index < perCoreStacks_.Length);
            if (Unsafe.Add(ref perCoreStacks_Root, index).TryPush(element))
                return;

            if (++index == perCoreStacks_.Length)
                index = 0;
        }
        SlowPath2(index, element);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void SlowPath2(int index, TElement element)
        {
            SharedPerCoreStack[] perCoreStacks = SharedPool<TElement, TElement>.PerCoreStacks;
            ref SharedPerCoreStack perCoreStacks_Root = ref Utils.GetArrayDataReference(perCoreStacks);
            // Next, transfer a per-core stack to the global reserve.
            Debug.Assert(index < perCoreStacks.Length);
            Unsafe.Add(ref perCoreStacks_Root, index).MoveToGlobalReserve(
                element,
                ref SharedPool<TElement, TElement>.GlobalReserve,
                ref SharedPool<TElement, TElement>.GlobalReserveCount
            );
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static SharedThreadLocalElementDisposableAtomic<TElement> SlowPath()
        {
            SharedThreadLocalElement result = SharedPoolHelpers.GetOrCreateThreadLocal(
                ref SharedPool<TElement, TElement>.ThreadLocalElement,
                new SharedThreadLocalElementDisposableAtomic<TElement>(), true,
                ref SharedPool<TElement, TElement>.AllThreadLocalElements,
                ref SharedPool<TElement, TElement>.AllThreadLocalElementsCount
            );
            Debug.Assert(result is SharedThreadLocalElementDisposableAtomic<TElement>);
            return Unsafe.As<SharedThreadLocalElementDisposableAtomic<TElement>>(result);
        }
    }

    /// <inheritdoc cref="ObjectPool{T}.Trim(bool)"/>
    public override void Trim(bool force = false)
    {
        SharedTrimInfo info = new(force);

        info.TryTrimThreadLocalElements<ValueDisposableAtomic<TElement>>(
            ref SharedPool<TElement, TElement>.AllThreadLocalElements,
            ref SharedPool<TElement, TElement>.AllThreadLocalElementsCount
        );

        info.TryTrimPerCoreStacks<ValueDisposableAtomic<TElement>>(SharedPool<TElement, TElement>.PerCoreStacks);

        info.TryTrimGlobalReserve<ValueDisposableAtomic<TElement>>(
            ref Unsafe.As<TElement?[]?, Array?>(ref SharedPool<TElement, TElement>.GlobalReserve),
            ref SharedPool<TElement, TElement>.GlobalReserveCount,
            ref SharedPool<TElement, TElement>.GlobalReserveMillisecondsTimeStamp
        );
    }
}
#endif