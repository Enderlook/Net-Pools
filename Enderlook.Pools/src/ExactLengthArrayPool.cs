using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Enderlook.Pools;

/// <summary>
/// Represent a pool of arrays of exact length.
/// </summary>
/// <typeparam name="T">Type of object to pool.</typeparam>
public abstract class ExactLengthArrayPool<T> : ArrayPool<T>
{
    internal Dictionary<int, ObjectPool<T[]>>? adapters;

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
    /// <param name="length">Length of arrays.</param>
    /// <param name="clearArrayOnReturn"> If <see langword="true"/> and if the pool will store a buffer that is being returned to enable subsequent reuse, will clear the array of its contents so that a subsequent consumer will not see the previous consumer's content.<br/>
    /// If <see langword="false"/> or if the pool will release the buffer, the array's contents are left unchanged.</param>
    /// <returns>Wrapper of pool.</returns>
    public static ArrayObjectPool<T> SharedOfLength(int length, bool clearArrayOnReturn = false) => SharedExactLengthArrayObjectPool<T>.GetPool(length, clearArrayOnReturn);

    /// <summary>
    /// Produces a wrapper <see cref="ObjectPool{T}"/> that uses the current instance to create arrays of the specified length.<br/>
    /// </summary>
    /// <param name="length">Length of arrays.</param>
    /// <param name="clearArrayOnReturn"> If <see langword="true"/> and if the pool will store a buffer that is being returned to enable subsequent reuse, will clear the array of its contents so that a subsequent consumer will not see the previous consumer's content.<br/>
    /// If <see langword="false"/> or if the pool will release the buffer, the array's contents are left unchanged.</param>
    /// <returns>Wrapper of pool.</returns>
    public virtual ArrayObjectPool<T> OfLength(int length, bool clearArrayOnReturn = false)
    {
        int key = clearArrayOnReturn ? -length : length;
        Dictionary<int, ObjectPool<T[]>>? adapters = this.adapters;
        if (adapters is null)
            return Fallback1();

        lock (adapters)
        {
            if (adapters.TryGetValue(key, out ObjectPool<T[]>? pool))
            {
                Debug.Assert(pool is ExactLengthArrayPoolAdapter<T>);
                return Unsafe.As<ExactLengthArrayPoolAdapter<T>>(pool);
            }
            return Fallback2();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        ExactLengthArrayPoolAdapter<T> Fallback1()
        {
            Dictionary<int, ObjectPool<T[]>>? adapters = [];
            Interlocked.CompareExchange(ref this.adapters, adapters, null);
            adapters = this.adapters;
            lock (adapters)
            {
                if (adapters.TryGetValue(key, out ObjectPool<T[]>? pool))
                {
                    Debug.Assert(pool is ExactLengthArrayPoolAdapter<T>);
                    return Unsafe.As<ExactLengthArrayPoolAdapter<T>>(pool);
                }
                return Fallback2();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        ExactLengthArrayPoolAdapter<T> Fallback2()
        {
            Dictionary<int, ObjectPool<T[]>>? adapters = this.adapters;
            Debug.Assert(adapters is not null);
            ExactLengthArrayPoolAdapter<T> pool = new(this, length, clearArrayOnReturn);
            adapters.Add(key, pool);
            return pool;
        }
    }

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