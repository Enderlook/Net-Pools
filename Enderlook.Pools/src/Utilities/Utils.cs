using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Enderlook.Pools;

internal static class Utils
{
    // Never change this value of `MinusOneExchange` will change.
    public const int LOCKED = -1;

    public enum MemoryPressure
    {
        Low,
        Medium,
        High
    }

    public static MemoryPressure GetMemoryPressure()
    {
#if NET5_0_OR_GREATER
        const double HighPressureThreshold = .90; // Percent of GC memory pressure threshold we consider "high".
        const double MediumPressureThreshold = .70; // Percent of GC memory pressure threshold we consider "medium".

        GCMemoryInfo memoryInfo = GC.GetGCMemoryInfo();

        if (memoryInfo.MemoryLoadBytes >= memoryInfo.HighMemoryLoadThresholdBytes * HighPressureThreshold)
            return MemoryPressure.High;

        if (memoryInfo.MemoryLoadBytes >= memoryInfo.HighMemoryLoadThresholdBytes * MediumPressureThreshold)
            return MemoryPressure.Medium;

        return MemoryPressure.Low;
#else
        return MemoryPressure.High;
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T GetArrayDataReference<T>(T[] array)
    {
#if NET5_0_OR_GREATER
        return ref MemoryMarshal.GetArrayDataReference(array);
#else
        return ref MemoryMarshal.GetReference((Span<T>)array);
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T NullExchange<T>(ref T? slot)
        where T : class
    {
        SpinWait spinWait = new();
        T? current;
        while (true)
        {
            current = Interlocked.Exchange(ref slot, null);
            if (current is not null)
                break;
            spinWait.SpinOnce();
        }
        return current;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MinusOneExchange(ref int slot)
    {
        SpinWait spinWait = new();
        int current;
        while (true)
        {
            current = Interlocked.Exchange(ref slot, LOCKED);
            if (current != LOCKED)
                break;
            spinWait.SpinOnce();
        }
        return current;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MinusOneRead(ref int slot)
    {
        SpinWait spinWait = new();
        int current;
        while (true)
        {
            current = Volatile.Read(ref slot);
            if (current != LOCKED)
                break;
            spinWait.SpinOnce();
        }
        return current;
    }

    [DoesNotReturn]
    public static void ThrowArgumentNullException_Array()
        => throw new ArgumentNullException("array");

    [DoesNotReturn]
    public static void ThrowArgumentNullException_Element()
        => throw new ArgumentNullException("element");

    public static void ThrowArgumentOutOfRangeException_ArrayLength()
        => throw new ArgumentOutOfRangeException("element", "Doesn't match expected array length.");

    public static void ThrowArgumentOutOfRangeException_ReserveCanNotBeNegative()
        => throw new ArgumentOutOfRangeException("reserve", "Can't be negative.");

    public static void ThrowArgumentOutOfRangeException_CapacityCanNotBeLowerThanOne()
        => throw new ArgumentOutOfRangeException("capacity", "Can't be lower than 1.");
}