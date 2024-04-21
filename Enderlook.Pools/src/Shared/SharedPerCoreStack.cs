using System;
using System.Runtime.CompilerServices;

namespace Enderlook.Pools;

internal struct SharedPerCoreStack
{
    public readonly Array Array;
    public int Count;
    public int MillisecondsTimeStamp;

    public SharedPerCoreStack(Array array)
    {
        Array = array;
        Count = 0;
        MillisecondsTimeStamp = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetCount() => Utils.MinusOneRead(ref Count);

    public static int GetApproximatedCountOf(SharedPerCoreStack[] array)
    {
        ref SharedPerCoreStack current = ref Utils.GetArrayDataReference(array);
        ref SharedPerCoreStack end = ref Unsafe.Add(ref current, array.Length);
        int count = 0;
        while (Unsafe.IsAddressLessThan(ref current, ref end))
        {
            count += current.GetCount();
            current = ref Unsafe.Add(ref current, 1);
        }
        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int StartTrim(int currentMilliseconds, int trimMilliseconds, int trimCount, out int oldCount, out int newCount)
    {
        int result;
        if (Count == 0)
        {
            result = 0;
            goto end1;
        }

        int oldCount_ = oldCount = Utils.MinusOneExchange(ref Count);
        if (oldCount_ == 0)
        {
            result = 1;
            goto end2;
        }

        int millisecondsTimeStamp = MillisecondsTimeStamp;
        if (millisecondsTimeStamp == 0)
        {
            millisecondsTimeStamp = currentMilliseconds;
            MillisecondsTimeStamp = millisecondsTimeStamp;
            result = 1;
            goto end2;
        }

        if ((currentMilliseconds - millisecondsTimeStamp) <= trimMilliseconds)
        {
            result = 1;
            goto end2;
        }

        // We've elapsed enough time since the first item went into the stack.
        // Drop the top item so it can be collected and make the stack look a little newer.

        newCount = Math.Max(oldCount - trimCount, 0);

        MillisecondsTimeStamp = oldCount > 0 ?
            millisecondsTimeStamp + (trimMilliseconds / 4) // Give the remaining items a bit more time.
            : 0;

        return 2;

    end1:
        oldCount = 0;
    end2:
#if NET5_0_OR_GREATER
        Unsafe.SkipInit(out newCount);
#else
        newCount = 0;
#endif
        return result;
    }
}