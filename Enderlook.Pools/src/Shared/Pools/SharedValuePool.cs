using Enderlook.Pools.Free;

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Enderlook.Pools;

internal sealed class SharedValuePool<
#if NET5_0_OR_GREATER
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
#endif
TElement, THelper> : ObjectPool<TElement>
    where THelper : ISharedPoolHelperValue
#if !NET7_0_OR_GREATER
        , new()
#endif
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
            SharedThreadLocalElementValueNonAtomic<TElement>? sharedThreadLocalElement = Unsafe.As<SharedThreadLocalElementValueNonAtomic<TElement>?>(current.Target);
            if (sharedThreadLocalElement is not null
                && Utils.MinusOneRead(ref sharedThreadLocalElement.Lock) == SharedThreadLocalElementValueNonAtomic<TElement>.HAVE)
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
            Debug.Assert(threadLocalElement is SharedThreadLocalElementValueNonAtomic<TElement>);
            SharedThreadLocalElementValueNonAtomic<TElement> threadLocalElement_ = Unsafe.As<SharedThreadLocalElementValueNonAtomic<TElement>>(threadLocalElement);
            // Check if the thread local has an element.
            // In case of being empty it's not useful here.
            // In case of being locked, we don't want to wait so we ignore it.
            if (threadLocalElement_.Lock == SharedThreadLocalElementValueNonAtomic<TElement>.HAVE)
            {
                int @lock = Interlocked.Exchange(ref threadLocalElement_.Lock, SharedThreadLocalElementValueNonAtomic<TElement>.LOCKED);
                if (@lock == SharedThreadLocalElementValueNonAtomic<TElement>.HAVE)
                {
                    TElement element = threadLocalElement_.Value!;
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
                    if (RuntimeHelpers.IsReferenceOrContainsReferences<TElement>())
#endif
                        threadLocalElement_.Value = default;
                    threadLocalElement_.Lock = SharedThreadLocalElementValueNonAtomic<TElement>.NOT_HAVE;
                    return element;
                }
            }
        }

        // Next, try to get an element from one of the per-core stacks.
        SharedPerCoreStack[] perCoreStacks = SharedPool<TElement, TElement>.PerCoreStacks;
        ref SharedPerCoreStack perCoreStacksRoot = ref Utils.GetArrayDataReference(perCoreStacks);
        // Try to pop from the associated stack first.
        // If that fails, try with other stacks.
        int index = SharedPoolHelpers.GetStartingIndex();
        bool failedAttempt = false;
        for (int i = 0; i < perCoreStacks.Length; i++)
        {
            Debug.Assert(index < perCoreStacks.Length);
            // TODO: This Unsafe.Add could be improved to avoid the under the hood multiplication (`base + offset * size` and just do `base + offset`).
            int value = Unsafe.Add(ref perCoreStacksRoot, index).TryPop(out TElement element2, false);
            if (value > 0)
                return element2;
            failedAttempt |= value == -1;

            if (++index == perCoreStacks.Length)
                index = 0;
        }

        if (failedAttempt)
            // If failed to pop from stack due to contention, try again but this time force the pop, which will wait for the lock to be released.
            return SlowPathForce();

        // Next, try to fill a per-core stack with objects from the global reserve.
        return SlowPath();

        static TElement SlowPathForce()
        {
            SharedPerCoreStack[] perCoreStacks = SharedPool<TElement, TElement>.PerCoreStacks;
            ref SharedPerCoreStack perCoreStacksRoot = ref Utils.GetArrayDataReference(perCoreStacks);
            // Try to pop from the associated stack first.
            // If that fails, try with other stacks.
            int index = SharedPoolHelpers.GetStartingIndex();
            for (int i = 0; i < perCoreStacks.Length; i++)
            {
                Debug.Assert(index < perCoreStacks.Length);
                // TODO: This Unsafe.Add could be improved to avoid the under the hood multiplication (`base + offset * size` and just do `base + offset`).
                if (Unsafe.Add(ref perCoreStacksRoot, index).TryPop(out TElement element2, true) > 0)
                    return element2;

                if (++index == perCoreStacks.Length)
                    index = 0;
            }

            // Next, try to fill a per-core stack with objects from the global reserve.
            return SlowPath();
        }

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
        Debug.Assert(threadLocalElement is null or SharedThreadLocalElementValueNonAtomic<TElement>);
        SharedThreadLocalElementValueNonAtomic<TElement> threadLocalElement_ = Unsafe.As<SharedThreadLocalElementValueNonAtomic<TElement>?>(threadLocalElement) ?? SlowPath();
        if (threadLocalElement_.Value is not null)
        {
            int @lock = Interlocked.Exchange(ref threadLocalElement_.Lock, SharedThreadLocalElementValueNonAtomic<TElement>.LOCKED);
            switch (@lock)
            {
                case SharedThreadLocalElementValueNonAtomic<TElement>.NOT_HAVE:
                {
                    threadLocalElement_.Value = element;
                    threadLocalElement_.Lock = SharedThreadLocalElementValueNonAtomic<TElement>.HAVE;
                    return;
                }
                case SharedThreadLocalElementValueNonAtomic<TElement>.HAVE:
                {
                    (element, threadLocalElement_.Value) = (threadLocalElement_.Value, element);
                    threadLocalElement_.Lock = SharedThreadLocalElementValueNonAtomic<TElement>.HAVE;
                    break;
                }
                // No need to set the lock if it was already locked.
                // And we don't plan to wait for it to unlock.
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static SharedThreadLocalElementValueNonAtomic<TElement> SlowPath()
        {
            SharedThreadLocalElement result = SharedPoolHelpers.GetOrCreateThreadLocal(
                ref SharedPool<TElement, TElement>.ThreadLocalElement,
#if NET7_0_OR_GREATER
                    THelper
#else
                    new THelper()
#endif
                    .NewLocal(),
#if NET7_0_OR_GREATER
                    THelper
#else
                    new THelper()
#endif
                    .HasLocalFinalizer,
                ref SharedPool<TElement, TElement>.AllThreadLocalElements,
                ref SharedPool<TElement, TElement>.AllThreadLocalElementsCount
            );
            Debug.Assert(result is SharedThreadLocalElementValueNonAtomic<TElement>);
            return Unsafe.As<SharedThreadLocalElementValueNonAtomic<TElement>>(result);
        }

        // Try to store the object from one of the per-core stacks.
        // We don't use a helper method, because calling it (even with aggressive inlining) produced an additional branching.
        SharedPerCoreStack[] perCoreStacks_ = SharedPool<TElement, TElement>.PerCoreStacks;
        ref SharedPerCoreStack perCoreStacks_Root = ref Utils.GetArrayDataReference(perCoreStacks_);
        // Try to push from the associated stack first.
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
    }

    /// <inheritdoc cref="ObjectPool{T}.Trim(bool)"/>
    public override void Trim(bool force = false)
    {
        SharedTrimInfo info = new(force);

        info.TryTrimThreadLocalElements<THelper>(
            ref SharedPool<TElement, TElement>.AllThreadLocalElements,
            ref SharedPool<TElement, TElement>.AllThreadLocalElementsCount
        );

        info.TryTrimPerCoreStacks<THelper>(SharedPool<TElement, TElement>.PerCoreStacks);

        info.TryTrimGlobalReserve<THelper>(
            ref Unsafe.As<TElement?[]?, Array?>(ref SharedPool<TElement, TElement>.GlobalReserve),
            ref SharedPool<TElement, TElement>.GlobalReserveCount,
            ref SharedPool<TElement, TElement>.GlobalReserveMillisecondsTimeStamp
        );
    }
}