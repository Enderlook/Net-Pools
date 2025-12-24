using Enderlook.Pools.Free;

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Enderlook.Pools;

internal sealed class SharedReferencePool<
#if NET5_0_OR_GREATER
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
#endif
TElement, THelper> : ObjectPool<TElement>
    where THelper : ISharedPoolHelperReference
#if !NET7_0_OR_GREATER
        , new()
#endif
{
    /// <inheritdoc cref="ObjectPool{T}.ApproximateCount"/>
    public override int ApproximateCount()
    {
        int count = SharedPerCoreStack.GetApproximatedCountOf(SharedPool<TElement, ObjectWrapper>.PerCoreStacks);

        count += SharedPoolHelpers.GetGlobalReserveCount(
            ref Unsafe.As<ObjectWrapper[]?, Array?>(ref SharedPool<TElement, ObjectWrapper>.GlobalReserve),
            ref SharedPool<TElement, ObjectWrapper>.GlobalReserveCount
        );

        count += SharedPoolHelpers.GetAllThreadLocalsCountReference(
            ref SharedPool<TElement, ObjectWrapper>.AllThreadLocalElements,
            ref SharedPool<TElement, ObjectWrapper>.AllThreadLocalElementsCount
        );

        return count;
    }

    /// <inheritdoc cref="ObjectPool{T}.Rent"/>
    public override TElement Rent()
    {
        SharedThreadLocalElement? threadLocalElement = SharedPool<TElement, ObjectWrapper>.ThreadLocalElement;
        if (threadLocalElement is not null)
        {
            Debug.Assert(threadLocalElement is SharedThreadLocalElementReference);
            SharedThreadLocalElementReference threadLocalElement_ = Unsafe.As<SharedThreadLocalElementReference>(threadLocalElement);
            object? value =
#if NET7_0_OR_GREATER
                THelper
#else
                new THelper()
#endif
                .Pop(threadLocalElement_);
            if (value is not null)
            {
                Debug.Assert(value is TElement);
                return Unsafe.As<object, TElement>(ref value);
            }
        }

        // We don't use a helper method, because calling it (even with aggressive inlining) produces unnecessary code.

        // Next, try to get an element from one of the per-core stacks.
        SharedPerCoreStack[] perCoreStacks = SharedPool<TElement, ObjectWrapper>.PerCoreStacks;
        ref SharedPerCoreStack perCoreStacksRoot = ref Utils.GetArrayDataReference(perCoreStacks);
        // Try to pop from the associated stack first.
        // If that fails, try with other stacks.
        int index = SharedPoolHelpers.GetStartingIndex();
        bool failedAttempt = false;
        for (int i = 0; i < perCoreStacks.Length; i++)
        {
            Debug.Assert(index < perCoreStacks.Length);
            // TODO: This Unsafe.Add could be improved to avoid the under the hood multiplication (`base + offset * size` and just do `base + offset`).
            int value = Unsafe.Add(ref perCoreStacksRoot, index).TryPop(out ObjectWrapper element2, false);
            if (value > 0)
            {
                Debug.Assert(element2.Value is TElement);
                TElement? element3 = Unsafe.As<object?, TElement?>(ref element2.Value);
                Debug.Assert(element3 is not null);
                return element3;
            }
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
            SharedPerCoreStack[] perCoreStacks = SharedPool<TElement, ObjectWrapper>.PerCoreStacks;
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
                    Debug.Assert(element2.Value is TElement);
                    TElement? element3 = Unsafe.As<object?, TElement?>(ref element2.Value);
                    Debug.Assert(element3 is not null);
                    return element3;
                }

                if (++index == perCoreStacks.Length)
                    index = 0;
            }

            // Next, try to fill a per-core stack with objects from the global reserve.
            return SlowPath();
        }

        static TElement SlowPath()
        {
            int count = SharedPool<TElement, ObjectWrapper>.GlobalReserveCount;
            if (count > 0)
            {
                int index = SharedPoolHelpers.GetStartingIndex();

                if (Unsafe.Add(
                    ref Utils.GetArrayDataReference(SharedPool<TElement, ObjectWrapper>.PerCoreStacks),
                    index).FillFromGlobalReserve(
                    out ObjectWrapper element,
                    ref SharedPool<TElement, ObjectWrapper>.GlobalReserve!,
                    ref SharedPool<TElement, ObjectWrapper>.GlobalReserveCount))
                {
                    Debug.Assert(element.Value is TElement);
                    return Unsafe.As<object?, TElement>(ref element.Value!);
                }
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
        SharedThreadLocalElement? threadLocalElement = SharedPool<TElement, ObjectWrapper>.ThreadLocalElement;
        Debug.Assert(threadLocalElement is null or SharedThreadLocalElementReference);
        SharedThreadLocalElementReference threadLocalElement_ = Unsafe.As<SharedThreadLocalElementReference?>(threadLocalElement) ?? SlowPath();
        object? old =
#if NET7_0_OR_GREATER
            THelper
#else
            new THelper()
#endif
            .Exchange(threadLocalElement_, element);
        threadLocalElement_.MillisecondsTimeStamp = 0;
        if (old is null)
            return;

        // Try to store the object from one of the per-core stacks.
        // We don't use a helper method, because calling it (even with aggressive inlining) produced an additional branching.
        SharedPerCoreStack[] perCoreStacks = SharedPool<TElement, ObjectWrapper>.PerCoreStacks;
        ref SharedPerCoreStack perCoreStacksRoot = ref Utils.GetArrayDataReference(perCoreStacks);
        // Try to push from the associated stack first.
        // If that fails, try with other stacks.
        int index = SharedPoolHelpers.GetStartingIndex();
        for (int i = 0; i < perCoreStacks.Length; i++)
        {
            Debug.Assert(index < perCoreStacks.Length);
            if (Unsafe.Add(ref perCoreStacksRoot, index).TryPush(new ObjectWrapper(old)))
                return;

            if (++index == perCoreStacks.Length)
                index = 0;
        }
        SlowPath2(index, old);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static SharedThreadLocalElementReference SlowPath()
        {
            SharedThreadLocalElement result = SharedPoolHelpers.GetOrCreateThreadLocal(
                ref SharedPool<TElement, ObjectWrapper>.ThreadLocalElement,
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
                ref SharedPool<TElement, ObjectWrapper>.AllThreadLocalElements,
                ref SharedPool<TElement, ObjectWrapper>.AllThreadLocalElementsCount
            );
            Debug.Assert(result is SharedThreadLocalElementReference);
            return Unsafe.As<SharedThreadLocalElementReference>(result);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void SlowPath2(int index, object element)
        {
            SharedPerCoreStack[] perCoreStacks = SharedPool<TElement, ObjectWrapper>.PerCoreStacks;
            ref SharedPerCoreStack perCoreStacks_Root = ref Utils.GetArrayDataReference(perCoreStacks);
            // Next, transfer a per-core stack to the global reserve.
            Debug.Assert(index < perCoreStacks.Length);
            Unsafe.Add(ref perCoreStacks_Root, index).MoveToGlobalReserve(
                new ObjectWrapper(element),
                ref SharedPool<TElement, ObjectWrapper>.GlobalReserve,
                ref SharedPool<TElement, ObjectWrapper>.GlobalReserveCount
            );
        }
    }

    /// <inheritdoc cref="ObjectPool{T}.Trim(bool)"/>
    public override void Trim(bool force = false)
    {
        SharedTrimInfo info = new(force);

        info.TryTrimThreadLocalElements<THelper>(
            ref SharedPool<TElement, ObjectWrapper>.AllThreadLocalElements,
            ref SharedPool<TElement, ObjectWrapper>.AllThreadLocalElementsCount
        );

        info.TryTrimPerCoreStacks<THelper>(SharedPool<TElement, ObjectWrapper>.PerCoreStacks);

        info.TryTrimGlobalReserve<THelper>(
            ref Unsafe.As<ObjectWrapper[]?, Array?>(ref SharedPool<TElement, ObjectWrapper>.GlobalReserve),
            ref SharedPool<TElement, ObjectWrapper>.GlobalReserveCount,
            ref SharedPool<TElement, ObjectWrapper>.GlobalReserveMillisecondsTimeStamp
        );
    }
}