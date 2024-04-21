using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Enderlook.Pools;

internal struct SharedPerCoreStack<T>
{
    private readonly T?[] array;
    private int count;
    private int millisecondsTimeStamp;

    public SharedPerCoreStack(T?[] array)
    {
        this.array = array;
        count = 0;
        millisecondsTimeStamp = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetCount() => Utils.MinusOneRead(ref count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPush(T element)
    {
        T?[] items = array;

        int count_ = Utils.MinusOneExchange(ref count);

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
        T?[] items = array;

        int count_ = Utils.MinusOneExchange(ref count);

        int newCount = count_ - 1;
        if (unchecked((uint)newCount < (uint)items.Length))
        {
            Debug.Assert(newCount < items.Length);
            ref T? slot = ref Unsafe.Add(ref Utils.GetArrayDataReference(items), newCount);
            Debug.Assert(slot is not null);
            element = slot;
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
#endif
                slot = default;
            count = newCount;
            return true;
        }

        count = count_;
#if NET5_0_OR_GREATER
        Unsafe.SkipInit(out element);
#else
        element = default;
#endif
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool FillFromGlobalReserve(out T? element, ref T?[]? globalReserve, ref int globalReserveCount)
    {
        int count_ = Utils.MinusOneExchange(ref count);
        T?[] globalReserve_ = Utils.NullExchange(ref globalReserve);

        int globalCount = globalReserveCount;
        bool found;
        if (globalCount > 0)
        {
            Debug.Assert(globalCount - 1 < globalReserve_.Length);
            element = Unsafe.Add(ref Utils.GetArrayDataReference(globalReserve_), --globalCount);
            found = true;
            Debug.Assert(element is not null);

            T?[] items = array;

            int length = Math.Min(Utils.MaxObjectsPerCore - count_, globalCount);
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
    public void MoveToGlobalReserve(T obj, ref T?[]? globalReserve, ref int globalReserveCount)
    {
        int count_ = Utils.MinusOneExchange(ref count);
        T?[] globalReserve_ = Utils.NullExchange(ref globalReserve);

        T?[] items = array;
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

        T?[] items = array;

        int oldCount = Utils.MinusOneExchange(ref count);
        if (oldCount == 0)
            goto end;

        int millisecondsTimeStamp = this.millisecondsTimeStamp;
        if (millisecondsTimeStamp == 0)
        {
            millisecondsTimeStamp = currentMilliseconds;
            goto endAndAssign;
        }

        if ((currentMilliseconds - millisecondsTimeStamp) <= trimMilliseconds)
            goto end;

        // We've elapsed enough time since the first item went into the stack.
        // Drop the top item so it can be collected and make the stack look a little newer.

        int newCount = Math.Max(oldCount - trimCount, 0);
        Array.Clear(items, newCount, oldCount - newCount);

        millisecondsTimeStamp = oldCount > 0 ?
            millisecondsTimeStamp + (trimMilliseconds / 4) // Give the remaining items a bit more time.
            : 0;

    endAndAssign:
        this.millisecondsTimeStamp = millisecondsTimeStamp;
    end:
        count = oldCount;
    }
}