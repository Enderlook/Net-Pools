using Enderlook.Pools.Free;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml.Linq;

namespace Enderlook.Pools;

internal sealed class SharedReferencePool<TElement, THelper> : ObjectPool<TElement>
    where THelper : ISharedPoolHelperReference
#if !NET7_0_OR_GREATER
        , new()
#endif
{
    private List<string> q = new();

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
        /*SharedThreadLocalElement? threadLocalElement = SharedPool<TElement, ObjectWrapper>.ThreadLocalElement;
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
        }*/

        // We don't use a helper method, because calling it (even with aggressive inlining) produces unnecessary code.

        //return ObjectPoolHelper<TElement>.Create();

        // Next, try to get an element from one of the per-core stacks.
        SharedPerCoreStack[] perCoreStacks = SharedPool<TElement, ObjectWrapper>.PerCoreStacks;
        ref SharedPerCoreStack perCoreStacksRoot = ref Utils.GetArrayDataReference(perCoreStacks);
        // Try to pop from the associated stack first.
        // If that fails, try with other stacks.
        int index = SharedPoolHelpers.GetStartingIndex();
        for (int i = 0; i < perCoreStacks.Length; i++)
        {
            Debug.Assert(index < perCoreStacks.Length);
            // TODO: This Unsafe.Add could be improved to avoid the under the hood multiplication (`base + offset * size` and just do `base + offset`).
            if (Unsafe.Add(ref perCoreStacksRoot, index).TryPop(out ObjectWrapper element2))
            {
                Debug.Assert(element2.Value is TElement);
                SharedPerCoreStack.AssertHasNot(perCoreStacks, element2, -1);
                lock (q)
                    q.Add($"Remove {index}");
                return Unsafe.As<object?, TElement>(ref element2.Value);
            }

            if (++index == perCoreStacks.Length)
                index = 0;
        }

        // Next, try to fill a per-core stack with objects from the global reserve.
        return SlowPath();

        static TElement SlowPath()
        {
            return ObjectPoolHelper<TElement>.Create();

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

       /* // Store the element into the thread local field.
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
            return;*/

        object? old = element;

        // Try to store the object from one of the per-core stacks.
        // We don't use a helper method, because calling it (even with aggressive inlining) produced an additional branching.
        SharedPerCoreStack[] perCoreStacks = SharedPool<TElement, ObjectWrapper>.PerCoreStacks;

        SharedPerCoreStack.AssertHasNot(perCoreStacks, new ObjectWrapper(element), -1);

        ref SharedPerCoreStack perCoreStacksRoot = ref Utils.GetArrayDataReference(perCoreStacks);
        // Try to push from the associated stack first.
        // If that fails, try with other stacks.
        int index = SharedPoolHelpers.GetStartingIndex();
        for (int i = 0; i < perCoreStacks.Length; i++)
        {
            Debug.Assert(index < perCoreStacks.Length);
            SharedPerCoreStack.AssertHasNot(perCoreStacks, new ObjectWrapper(element), index);
            if (Unsafe.Add(ref perCoreStacksRoot, index).TryPush(new ObjectWrapper(old)))
            {
                //SharedPerCoreStack.AssertHasNot(perCoreStacks, new ObjectWrapper(element), index);
                lock (q)
                    q.Add($"Add {index}");
                return;
            }
            //SharedPerCoreStack.AssertHasNot(perCoreStacks, new ObjectWrapper(element), index);

            if (++index == perCoreStacks.Length)
                index = 0;
        }
        SharedPerCoreStack.AssertHasNot(perCoreStacks, new ObjectWrapper(element), -1);
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
        static void SlowPath2(int index, object obj)
        {
            return;
            SharedPerCoreStack[] perCoreStacks = SharedPool<TElement, ObjectWrapper>.PerCoreStacks;
            ref SharedPerCoreStack perCoreStacks_Root = ref Utils.GetArrayDataReference(perCoreStacks);
            // Next, transfer a per-core stack to the global reserve.
            Debug.Assert(index < perCoreStacks.Length);
            Unsafe.Add(ref perCoreStacks_Root, index).MoveToGlobalReserve(
                new ObjectWrapper(obj),
                ref SharedPool<TElement, ObjectWrapper>.GlobalReserve,
                ref SharedPool<TElement, ObjectWrapper>.GlobalReserveCount
            );
        }
    }

    /// <inheritdoc cref="ObjectPool{T}.Trim(bool)"/>
    public override void Trim(bool force = false)
    {
        SharedTrimInfo info = new(force);

        //HashSet<object> s = new();
        Dictionary<object, int> s = new();

        /*foreach (var l in (SharedPool<TElement, ObjectWrapper>.AllThreadLocalElements ?? Array.Empty<GCHandle>()).Take(SharedPool<TElement, ObjectWrapper>.AllThreadLocalElementsCount))
        {
            var t = l.Target;
            if (t is not null)
            {
                object? v = ((SharedThreadLocalElementReference)t).Value;
                if (v is not null)
                    Debug.Assert(s.Add(v));
            }
        }*/

        //SharedPerCoreStack[] perCoreStacks = SharedPool<TElement, ObjectWrapper>.PerCoreStacks;
        var t = new SharedPerCoreStack[SharedPoolHelpers.PerCoreStacksCount];
        for (int i = 0; i < t.Length; i++)
        {
            t[i] = new(new ObjectWrapper[SharedPoolHelpers.MaxObjectsPerCore]);
        }

        SharedPerCoreStack[] perCoreStacks = Interlocked.Exchange(ref SharedPool<TElement, ObjectWrapper>.PerCoreStacks, t);
        /*bool[] taken = new bool[perCoreStacks.Length];
        for (int i = 0; i < perCoreStacks.Length; i++)
        {
            Monitor.Enter(perCoreStacks[i].o, ref taken[i]);
        }*/

        for (int i = 0; i < perCoreStacks.Length; i++)
        {
            ref var l = ref perCoreStacks[i];
            int k = Utils.MinusOneExchange(ref l.Count);
            var o = (ObjectWrapper[])l.Array;
            for (int j = 0; j < k; j++)
            {
                object? v = o[j].Value;
                if (v is not null)
                {
                    bool q = s.TryGetValue(v, out var p);
                    Debug.Assert(s.TryAdd(v, i));
                    //Return((TElement)v);
                }
            }
            l.Count = k;
        }

        foreach (var e in s.Keys)
            Return((TElement)e);

        /*for (int i = 0; i < perCoreStacks.Length; i++)
        {
            if (taken[i])
            {
                Monitor.Exit(perCoreStacks[i].o);
            }
        }*/

        /*info.TryTrimThreadLocalElements<THelper>(
            ref SharedPool<TElement, ObjectWrapper>.AllThreadLocalElements,
            ref SharedPool<TElement, ObjectWrapper>.AllThreadLocalElementsCount
        );*/

        info.TryTrimPerCoreStacks<THelper>(SharedPool<TElement, ObjectWrapper>.PerCoreStacks);

        /*info.TryTrimGlobalReserve<THelper>(
            ref Unsafe.As<ObjectWrapper[]?, Array?>(ref SharedPool<TElement, ObjectWrapper>.GlobalReserve),
            ref SharedPool<TElement, ObjectWrapper>.GlobalReserveCount,
            ref SharedPool<TElement, ObjectWrapper>.GlobalReserveMillisecondsTimeStamp
        );*/
    }
}