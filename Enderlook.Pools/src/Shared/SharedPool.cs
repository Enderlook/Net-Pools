using System;
using System.Runtime.InteropServices;

namespace Enderlook.Pools;

internal struct SharedPool<TElement, TStorage>
{
    // Inspired from https://source.dot.net/#System.Private.CoreLib/TlsOverPerCoreLockedStacksArrayPool.cs

    /// <summary>
    /// A per-thread element for better cache.
    /// </summary>
    [ThreadStatic]
    public static SharedThreadLocalElement? ThreadLocalElement;

    /// <summary>
    /// Used to keep tack of all thread local objects for trimming if needed.
    /// </summary>
    public static GCHandle[]? AllThreadLocalElements = new GCHandle[Environment.ProcessorCount];

    /// <summary>
    /// Keep tracks of the amount of used slots in <see cref="AllThreadLocalElements"/>.
    /// </summary>
    public static int AllThreadLocalElementsCount;

    /// <summary>
    /// An array of per-core objects.<br/>
    /// The slots are lazily initialized.
    /// </summary>
    public static/* readonly*/ SharedPerCoreStack[] PerCoreStacks = new SharedPerCoreStack[SharedPoolHelpers.PerCoreStacksCount];

    /// <summary>
    /// A global dynamic-size reserve of elements.<br/>
    /// When all <see cref="PerCoreStacks"/> get fulls, one of them is emptied and all its objects are moved here.<br/>
    /// When all <see cref="PerCoreStacks"/> get empty, one of them is fulled with objects from this reserve.<br/>
    /// Those operations are done in a batch to reduce the amount of times this requires to be acceded.
    /// </summary>
    public static TStorage?[]? GlobalReserve = new TStorage[SharedPoolHelpers.MaxObjectsPerCore];

    /// <summary>
    /// Keep tracks of the amount of used slots in <see cref="GlobalReserve"/>.
    /// </summary>
    public static int GlobalReserveCount;

    /// <summary>
    /// Keep record of last time <see cref="GlobalReserve"/> was trimmed;
    /// </summary>
    public static int GlobalReserveMillisecondsTimeStamp;

    static SharedPool()
    {
        SharedPerCoreStack[] perCoreStacks = PerCoreStacks;
        for (int i = 0; i < perCoreStacks.Length; i++)
            perCoreStacks[i] = new SharedPerCoreStack(new TStorage[SharedPoolHelpers.MaxObjectsPerCore]);
    }
}