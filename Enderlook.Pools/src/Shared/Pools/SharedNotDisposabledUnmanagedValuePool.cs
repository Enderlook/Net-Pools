#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
using Enderlook.Pools.Free;

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Enderlook.Pools;

internal sealed class SharedNotDisposabledUnmanagedValuePool<TElement> : ObjectPool<TElement>
{
    // Inspired from https://source.dot.net/#System.Private.CoreLib/TlsOverPerCoreLockedStacksArrayPool.cs

    /// <summary>
    /// A per-thread element for better cache.
    /// </summary>
    [ThreadStatic]
    public static NullableS<TElement> ThreadLocalElement;

    /// <summary>
    /// An array of per-core objects.<br/>
    /// The slots are lazily initialized.
    /// </summary>
    public static readonly SharedPerCoreStack[] PerCoreStacks = new SharedPerCoreStack[SharedPoolHelpers.PerCoreStacksCount];

    /// <summary>
    /// A global dynamic-size reserve of elements.<br/>
    /// When all <see cref="PerCoreStacks"/> get fulls, one of them is emptied and all its objects are moved here.<br/>
    /// When all <see cref="PerCoreStacks"/> get empty, one of them is fulled with objects from this reserve.<br/>
    /// Those operations are done in a batch to reduce the amount of times this requires to be acceded.
    /// </summary>
    public static TElement[]? GlobalReserve = new TElement[SharedPoolHelpers.MaxObjectsPerCore];

    /// <summary>
    /// Keep tracks of the amount of used slots in <see cref="GlobalReserve"/>.
    /// </summary>
    public static int GlobalReserveCount;

    /// <summary>
    /// Keep record of last time <see cref="GlobalReserve"/> was trimmed;
    /// </summary>
    public static int GlobalReserveMillisecondsTimeStamp;

    static SharedNotDisposabledUnmanagedValuePool()
    {
        SharedPerCoreStack[] perCoreStacks = PerCoreStacks;
        for (int i = 0; i < perCoreStacks.Length; i++)
            perCoreStacks[i] = new SharedPerCoreStack(new TElement[SharedPoolHelpers.MaxObjectsPerCore]);
    }

    /// <inheritdoc cref="ObjectPool{T}.ApproximateCount"/>
    public override int ApproximateCount()
    {
        int count = SharedPerCoreStack.GetApproximatedCountOf(PerCoreStacks);
        count += SharedPoolHelpers.GetGlobalReserveCount(ref Unsafe.As<TElement[]?, Array?>(ref GlobalReserve), ref GlobalReserveCount);
        return count;
    }

    /// <inheritdoc cref="ObjectPool{T}.Rent"/>
    public override TElement Rent()
    {
        ref NullableS<TElement> threadLocal = ref ThreadLocalElement;
        if (threadLocal.Has)
        {
            threadLocal.Has = false;
            return threadLocal.Value!;
        }

        // We don't use a helper method, because calling it (even with aggressive inlining) produces unnecessary code.

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
            SharedPerCoreStack[] perCoreStacks = PerCoreStacks;
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
            if (GlobalReserveCount > 0)
            {
                int index = SharedPoolHelpers.GetStartingIndex();

                if (Unsafe.Add(
                    ref Utils.GetArrayDataReference(PerCoreStacks),
                    index).FillFromGlobalReserve(
                    out TElement? element,
                    ref GlobalReserve!,
                    ref GlobalReserveCount))
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

        ref NullableS<TElement> threadLocal = ref ThreadLocalElement;
        if (!threadLocal.Has)
        {
            threadLocal.Has = true;
            threadLocal.Value = element;
            return;
        }

        (element, threadLocal.Value) = (threadLocal.Value!, element);

        // Try to store the object from one of the per-core stacks.
        // We don't use a helper method, because calling it (even with aggressive inlining) produced an additional branching.
        SharedPerCoreStack[] perCoreStacks_ = SharedPool<TElement, TElement>.PerCoreStacks;
        ref SharedPerCoreStack perCoreStacks_Root = ref Utils.GetArrayDataReference(perCoreStacks_);
        // Try to push from the associated stack first.
        // If that fails, try with other stacks.
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        int currentProcessorId = Thread.GetCurrentProcessorId();
#else
        int currentProcessorId = Thread.CurrentThread.ManagedThreadId; // TODO: This is probably a bad idea.
#endif
        int index = (int)((uint)currentProcessorId % (uint)SharedPoolHelpers.PerCoreStacksCount);
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

        info.TryTrimPerCoreStacks<UnmanagedValueNotDisposable<TElement>>(PerCoreStacks);

        info.TryTrimGlobalReserve<UnmanagedValueNotDisposable<TElement>>(
            ref Unsafe.As<TElement[]?, Array?>(ref GlobalReserve),
            ref GlobalReserveCount,
            ref GlobalReserveMillisecondsTimeStamp
        );
    }
}
#endif