﻿using System.Buffers;
using System.Runtime.CompilerServices;

namespace Enderlook.Pools;

/// <summary>
/// Represent a pool of arrays of exact length.
/// </summary>
/// <typeparam name="T">Type of object to pool.</typeparam>
public abstract class ExactLengthArrayPool<T> : ArrayPool<T>
{
    /// <summary>
    /// Retrieves a shared <see cref="ExactLengthArrayPool{T}"/> instance.<br/>
    /// The shared pool has the following features:
    /// <list type="bullet">
    ///     <item>Instantiates new elements when empty.</item>
    ///     <item>Resize itself to accommodate all returned elements to the pool.</item>
    ///     <item>Periodically trims itself removing old elements from the pool (GC-triggered).</item>
    ///     <item>Is thread-safe.</item>
    /// </list>
    /// </summary>
    public static new ExactLengthArrayPool<T> Shared
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => SharedExactLengthArrayPool<T>.Shared_;
    }

    /// <summary>
    /// Retrieves a shared <see cref="ObjectPool{T}"/> instance configured to create arrays of the specified length.<br/>
    /// The shared pool has the following features:
    /// <list type="bullet">
    ///     <item>Instantiates new elements when empty.</item>
    ///     <item>Resize itself to accommodate all returned elements to the pool.</item>
    ///     <item>Periodically trims itself removing old elements from the pool (GC-triggered).</item>
    ///     <item>Is thread-safe.</item>
    /// </list>
    /// </summary>
    public static ObjectPool<T[]> SharedOf(int length) => SharedExactLengthArrayObjectPool<T>.GetPool(length);

    /// <summary>
    /// Gets an approximate count of the objects stored in the pool.<br/>
    /// This value is not accurate and may be lower or higher than the actual count.<br/>
    /// This is primary used for debugging purposes.
    /// </summary>
    /// <returns>Approximate count of elements in the pool. If this operation is not supported, return -1 instead of throwing.</returns>
    public abstract int ApproximateCount();

    /// <summary>
    /// Trim the content of the pool.
    /// </summary>
    /// <param name="force">If <see langword="true"/>, the pool is forced to clear all elements inside. Otherwise, the pool may only clear partially or not clear at all if the heuristic says so.</param>
    public abstract void Trim(bool force = false);
}