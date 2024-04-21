using Enderlook.Pools.Free;

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Enderlook.Pools;

internal readonly struct SharedTrimInfo
{
    private const int GlobalShrinkThreshold = 4;
    private const int GlobalShrinkFactor = 2;

    public readonly Utils.MemoryPressure MemoryPressure;
    public readonly int PerCoreTrimMilliseconds;
    public readonly int PerCoreTrimCount;
    public readonly uint ThreadLocalTrimMilliseconds;
    public readonly int GlobalTrimMilliseconds;
    public readonly float GlobalTrimPercentage;
    public readonly int CurrentMilliseconds;

    public SharedTrimInfo(bool force)
    {
        const int PerCoreLowTrimAfterMilliseconds = 60 * 1000; // Trim after 60 seconds for low pressure.
        const int PerCoreMediumTrimAfterMilliseconds = 60 * 1000; // Trim after 60 seconds for moderate pressure.
        const int PerCoreHighTrimAfterMilliseconds = 10 * 1000; // Trim after 10 seconds for high pressure.
        const int PerCoreLowTrimCount = 1; // Trim 1 item when pressure is low.
        const int PerCoreMediumTrimCount = 2; // Trim 2 items when pressure is moderate.
        const int PerCoreHighTrimCount = SharedPoolHelpers.MaxObjectsPerCore; // Trim all items when pressure is high.

        const int ThreadLocalLowMilliseconds = 30 * 1000; // Trim after 30 seconds for moderate pressure.
        const int ThreadLocalMediumMilliseconds = 15 * 1000; // Trim after 15 seconds for low pressure.

        const int ReserveLowTrimAfterMilliseconds = 90 * 1000; // Trim after 90 seconds for low pressure.
        const int ReserveMediumTrimAfterMilliseconds = 45 * 1000; // Trim after 45 seconds for low pressure.
        const float ReserveLowTrimPercentage = .10f; // Trim 10% of elements for low pressure.
        const float ReserveMediumTrimPercentage = .30f; // Trim 30% of elements for moderate pressure.

        CurrentMilliseconds = Environment.TickCount;

        if (force)
        {
            MemoryPressure = Utils.MemoryPressure.High;
            PerCoreTrimCount = PerCoreHighTrimCount;
            // Forces to clear everything regardless of time.
            PerCoreTrimMilliseconds = 0;
            ThreadLocalTrimMilliseconds = 0;
            GlobalTrimMilliseconds = 0;
            GlobalTrimPercentage = 1;
        }
        else
        {
            MemoryPressure = Utils.GetMemoryPressure();
            switch (MemoryPressure)
            {
                case Utils.MemoryPressure.High:
                    PerCoreTrimCount = PerCoreHighTrimCount;
                    PerCoreTrimMilliseconds = PerCoreHighTrimAfterMilliseconds;
                    // Forces to clear everything regardless of time.
                    ThreadLocalTrimMilliseconds = 0;
                    GlobalTrimMilliseconds = 0;
                    GlobalTrimPercentage = 1;
                    break;
                case Utils.MemoryPressure.Medium:
                    PerCoreTrimCount = PerCoreMediumTrimCount;
                    PerCoreTrimMilliseconds = PerCoreMediumTrimAfterMilliseconds;
                    ThreadLocalTrimMilliseconds = ThreadLocalMediumMilliseconds;
                    GlobalTrimMilliseconds = ReserveMediumTrimAfterMilliseconds;
                    GlobalTrimPercentage = ReserveMediumTrimPercentage;
                    break;
                default:
                    Debug.Assert(MemoryPressure == Utils.MemoryPressure.Low);
                    PerCoreTrimCount = PerCoreLowTrimCount;
                    PerCoreTrimMilliseconds = PerCoreLowTrimAfterMilliseconds;
                    ThreadLocalTrimMilliseconds = ThreadLocalLowMilliseconds;
                    GlobalTrimMilliseconds = ReserveLowTrimAfterMilliseconds;
                    GlobalTrimPercentage = ReserveLowTrimPercentage;
                    break;
            }
        }
    }

    public void TryTrimPerCoreStacks<THelper>(SharedPerCoreStack[] array)
        where THelper : ISharedPoolHelper
#if !NET7_0_OR_GREATER
            , new()
#endif
    {
        int currentMilliseconds = CurrentMilliseconds;
        int trimMilliseconds = PerCoreTrimMilliseconds;
        int trimCount = PerCoreTrimCount;

        // Trim each of the per-core stacks.
        Debug.Assert(array.GetType() == typeof(SharedPerCoreStack[]));
        ref SharedPerCoreStack current = ref Utils.GetArrayDataReference(array);
        ref SharedPerCoreStack end = ref Unsafe.Add(ref current, array.Length);
        while (Unsafe.IsAddressLessThan(ref current, ref end))
        {
            switch (current.StartTrim(currentMilliseconds, trimMilliseconds, trimCount, out int oldCount, out int newCount))
            {
                case 2:
#if NET7_0_OR_GREATER
                    THelper
#else
                    new THelper()
#endif
                        .Free(current.Array, newCount, oldCount, false);
                    oldCount = newCount;
                    goto case 1;
                case 1:
                    current.Count = oldCount;
                    break;
            }
            current = ref Unsafe.Add(ref current, 1);
        }
    }

    public void TryTrimThreadLocalElements<THelper>(ref GCHandle[]? threadLocals, ref int count)
        where THelper : ISharedPoolHelper
#if !NET7_0_OR_GREATER
            , new()
#endif
    {
        int currentMilliseconds = CurrentMilliseconds;
        uint threadLocalsTrimMilliseconds = ThreadLocalTrimMilliseconds;

        // Trim each of the thread local fields.
        // Note that threads may be modifying their thread local fields concurrently with this trimming happening.
        // We do not force synchronization with those operations, so we accept the fact
        // that we may potentially trim an object we didn't need to.
        // Both of these should be rare occurrences.
        // This is fine as long as we don't call the Dispose of an already returned object.

        GCHandle[] handles = Utils.NullExchange(ref threadLocals);

        Debug.Assert(count <= handles.Length);

        ref GCHandle start = ref Utils.GetArrayDataReference(handles);
        ref GCHandle current = ref start;
        ref GCHandle newCurrent = ref start;
#if DEBUG
        int count_ = 0;
#endif
        ref GCHandle end = ref Unsafe.Add(ref start, count);

        // Under high pressure, we don't wait time to trim, so we release all thread locals.
        if (threadLocalsTrimMilliseconds == 0)
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
                SharedThreadLocalElement threadLocal = Unsafe.As<SharedThreadLocalElement>(target);

#if NET7_0_OR_GREATER
                THelper
#else
                new THelper()
#endif
                .TryFree(threadLocal);
                threadLocal.MillisecondsTimeStamp = 0;

                Debug.Assert(Unsafe.IsAddressLessThan(ref newCurrent, ref end));
                newCurrent = handle;
#if DEBUG
                Debug.Assert(count_++ < count);
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
                SharedThreadLocalElement threadLocal = Unsafe.As<SharedThreadLocalElement>(target);

                // We treat 0 to mean it hasn't yet been seen in a Trim call.
                // In the very rare case where Trim records 0, it'll take an extra Trim call to remove the object.
                int lastSeen = threadLocal.MillisecondsTimeStamp;
                if (lastSeen == 0)
                    threadLocal.MillisecondsTimeStamp = currentMilliseconds;
                else if ((currentMilliseconds - lastSeen) >= threadLocalsTrimMilliseconds)
                {
                    // Time noticeably wrapped, or we've surpassed the threshold.
                    // Clear out the slot.
#if NET7_0_OR_GREATER
                    THelper
#else
                    new THelper()
#endif
                    .TryFree(threadLocal);
                    threadLocal.MillisecondsTimeStamp = 0;
                }

                Debug.Assert(Unsafe.IsAddressLessThan(ref newCurrent, ref end));
                newCurrent = handle;
#if DEBUG
                Debug.Assert(count_++ < count);
#endif
                newCurrent = ref Unsafe.Add(ref newCurrent, 1);
                current = ref Unsafe.Add(ref current, 1);
            }
        }
        int newCount = (int)Unsafe.ByteOffset(ref start, ref newCurrent) / Unsafe.SizeOf<GCHandle>();
#if DEBUG
        Debug.Assert(newCount == count_);
#endif

        threadLocals = handles;
        count = newCount;
    }

    public void TryTrimGlobalReserve<THelper>(ref Array? globalReserve, ref int globalReserveCount, ref int globalReserveMilliseconsTimeStamp)
        where THelper : ISharedPoolHelper
#if !NET7_0_OR_GREATER
        , new()
#endif
    {
        Array array = Utils.NullExchange(ref globalReserve);
        int count = globalReserveCount;

        if (count == 0)
            globalReserveMilliseconsTimeStamp = 0;
        else
        {
            float globalTrimPercentage = GlobalTrimPercentage;

            // Under high pressure, we don't wait time to trim, so we remove all objects in reserve.
            if (globalTrimPercentage == 1)
            {
                Debug.Assert(GlobalTrimMilliseconds == 0);
                count = 0;
                // Note: If we made the method generic over element type,
                // `Array.Length` would be replaced with `T[].Length`
                // which is a bit faster.
                // Alternatively, `ISharedPoolHelper` could have a method for that.
                int length = array.Length;
                if (length <= SharedPoolHelpers.MaxObjectsPerCore)
                {
#if NET7_0_OR_GREATER
                    THelper
#else
                    new THelper()
#endif
                    .Free(array, 0, length, false);
                }
                else
                {
#if NET7_0_OR_GREATER
                    THelper
#else
                    new THelper()
#endif
                    .Free(array, 0, length, true);
                    array =
#if NET7_0_OR_GREATER
                    THelper
#else
                    new THelper()
#endif
                    .NewArray(SharedPoolHelpers.InitialGlobalReserveCapacity);
                }
                globalReserveMilliseconsTimeStamp = 0;
            }
            else
            {
                int currentMilliseconds = CurrentMilliseconds;
                int millisecondsStamp = globalReserveMilliseconsTimeStamp;
                if (millisecondsStamp == 0)
                    globalReserveMilliseconsTimeStamp = millisecondsStamp = currentMilliseconds;

                if ((currentMilliseconds - millisecondsStamp) > GlobalTrimMilliseconds)
                {
                    // Otherwise, remove a percentage of all stored objects in the reserve, based on how long was the last trimming.
                    // This time is approximate, with the time set not when the object is stored but when we see it during a Trim,
                    // so it takes at least two Trim calls (and thus two gen2 GCs) to drop objects, unless we're in high memory pressure.

                    int toRemove = (int)Math.Ceiling(count * GlobalTrimPercentage);
                    int newGlobalCount = Math.Max(count - toRemove, 0);
                    toRemove = count - newGlobalCount;
                    // Note: If we made the method generic over element type,
                    // `Array.Length` would be replaced with `T[].Length`
                    // which is a bit faster.
                    // Alternatively, `ISharedPoolHelper` could have a method for that.
                    int length = array.Length;
                    count = newGlobalCount;

                    // Since the global reserve has a dynamic size, we shrink the reserve if it gets too small.
                    if (length / newGlobalCount >= GlobalShrinkThreshold)
                    {
                        if (length <= (SharedPoolHelpers.InitialGlobalReserveCapacity * GlobalShrinkFactor))
                        {
                            // Reserve is already small, free elements to trim.
#if NET7_0_OR_GREATER
                            THelper
#else
                            new THelper()
#endif
                            .Free(array, newGlobalCount, toRemove, false);
                        }
                        else
                        {
                            // Reserve is big, can be resized to reduce size.
                            int newLength = length / GlobalShrinkFactor;
                            Array newArray =
#if NET7_0_OR_GREATER
                            THelper
#else
                            new THelper()
#endif
                            .NewArray(newLength);
                            Array.Copy(array, newArray, newGlobalCount);
                            // Free elements that we will trim.
#if NET7_0_OR_GREATER
                            THelper
#else
                            new THelper()
#endif
                            .Free(array, newGlobalCount, toRemove, true);
                            array = newArray;
                        }
                    }
                    else
                    {
                        // Reserve is already small, free elements to trim.
#if NET7_0_OR_GREATER
                        THelper
#else
                        new THelper()
#endif
                        .Free(array, newGlobalCount, toRemove, false);
                    }
                }
            }
        }

        globalReserveCount = count;
        globalReserve = array;
    }
}