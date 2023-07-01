using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Enderlook.Pools;

internal static class Utils
{
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

    [DoesNotReturn]
    public static void ThrowArgumentNullException_Element()
        => throw new ArgumentNullException("element");

    public static void ThrowArgumentOutOfRangeException_InitialColdCapacityCanNotBeNegative()
        => throw new ArgumentOutOfRangeException("initialColdCapacity", "Can't be negative.");

    public static void ThrowArgumentOutOfRangeException_InitialCapacityCanNotBeNegative()
        => throw new ArgumentOutOfRangeException("initialCapacity", "Can't be negative.");

    public static void ThrowArgumentOutOfRangeException_HotCapacityCanNotBeLowerThanOne()
        => throw new ArgumentOutOfRangeException("hotCapacity", "Can't be lower than 1.");
}