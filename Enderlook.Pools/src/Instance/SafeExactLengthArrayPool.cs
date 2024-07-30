using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Enderlook.Pools;

/// <summary>
/// A fast, dynamically-sized and thread-safe array pool to store arrays of specific lengths.<br/>
/// </summary>
/// <typeparam name="T">Type of element array to pool</typeparam>
public sealed class SafeExactLengthArrayPool<T> : ExactLengthArrayPool<T>
{
    private int capacity;
    private int reserve;

    private SafeExactLengthArrayObjectPool<T>? lastPool;

    private int adaptersCount;
    private SafeExactLengthArrayObjectPool<T>[] adaptersArray = new SafeExactLengthArrayObjectPool<T>[4];

    /// <summary>
    /// Capacity of the pool.<br/>
    /// This region of the pool support concurrent access.<br/>
    /// The capacity should preferably be not greater than <c><see cref="Environment.ProcessorCount"/> * 2</c>, since it's fully iterated before accessing the reserve.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Throw when <see langword="value"/> is lower than 1.</exception>
    public int Capacity
    {
        get => capacity + 1;
        init
        {
            if (value < 1) Utils.ThrowArgumentOutOfRangeException_CapacityCanNotBeLowerThanOne();
            value -= 1;  // -1 due to firstElement.
            capacity = value;
        }
    }

    /// <summary>
    /// Current capacity of the reserve.<br/>
    /// This reserve pool is only acceded when the non-reserve capacity gets full or empty.<br/>
    /// This is because this region can only be acceded by a single thread<br/>
    /// This pool has a dynamic size so this value represent the initial size of the pool which may enlarge or shrink over time.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Throw when <see langword="value"/> is negative.</exception>
    public int Reserve
    {
        get => reserve;
        init
        {
            if (value < 0) Utils.ThrowArgumentOutOfRangeException_ReserveCanNotBeNegative();
            reserve = value;
        }
    }

    /// <summary>
    /// Determines if the reserve pool is allowed to grow and shrink given its usage.
    /// </summary>
    public bool IsReserveDynamic { get; init; } = true;

    /// <summary>
    /// Creates a new instance of the pool.
    /// </summary>
    public SafeExactLengthArrayPool()
    {
        adapters = new();
        GCCallbackArray<T> _ = new(this);
    }

    /// <inheritdoc cref="ExactLengthArrayPool{T}.ApproximateCount"/>
    public override int ApproximateCount()
    {
        int count = 0;
        int adaptersCount = this.adaptersCount;
        SafeExactLengthArrayObjectPool<T>[] adaptersArray = this.adaptersArray;
        for (int i = 0; i < adaptersCount; i++)
            count += adaptersArray[i].ApproximateCount();
        return count;
    }

    /// <inheritdoc cref="ArrayPool{T}.Rent(int)"/>
    public override T[] Rent(int length)
    {
#if NET5_0_OR_GREATER
        return OfLength(length).Rent();
#else
        return OfLength_(length).Rent();
#endif
    }

    /// <inheritdoc cref="ArrayPool{T}.Return(T[], bool)"/>
    public override void Return(T[] array, bool clearArray = false)
    {
        if (array is null) return;
#if NET5_0_OR_GREATER
        OfLength(array.Length).Return_(array);
#else
        OfLength_(array.Length).Return_(array);
#endif
    }

    /// <inheritdoc cref="ExactLengthArrayPool{T}.Trim(bool)"/>
    public override void Trim(bool force = false)
    {
        int adaptersCount = this.adaptersCount;
        SafeExactLengthArrayObjectPool<T>[] adaptersArray = this.adaptersArray;
        for (int i = 0; i < adaptersCount; i++)
            adaptersArray[i].Trim(force);
    }

    /// <summary>
    /// Gives the internal <see cref="SafeExactLengthArrayObjectPool{T}"/> that uses the current instance to create arrays of the specified length.<br/>
    /// </summary>
    /// <param name="length">Length of arrays.</param>
    /// <returns>Wrapper of pool.</returns>
#if NET5_0_OR_GREATER
    public override SafeExactLengthArrayObjectPool<T> OfLength(int length)
#else
    public override ObjectPool<T[]> OfLength(int length) => OfLength_(length);
    private SafeExactLengthArrayObjectPool<T> OfLength_(int length)
#endif
    {
        SafeExactLengthArrayObjectPool<T>? pool = lastPool;
        if (pool?.Length == length)
            return pool;

        Dictionary<int, ObjectPool<T[]>>? adapters = this.adapters;
        if (adapters is null)
            return Fallback1();

        lock (adapters)
        {
            if (adapters.TryGetValue(length, out ObjectPool<T[]>? pool_))
            {
                Debug.Assert(pool_ is SafeExactLengthArrayObjectPool<T>);
                return lastPool = Unsafe.As<SafeExactLengthArrayObjectPool<T>>(pool_);
            }
            return Fallback2();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        SafeExactLengthArrayObjectPool<T> Fallback1()
        {
            Dictionary<int, ObjectPool<T[]>>? adapters = [];
            Interlocked.CompareExchange(ref this.adapters, adapters, null);
            adapters = this.adapters;
            lock (adapters)
                return Fallback2();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        SafeExactLengthArrayObjectPool<T> Fallback2()
        {
            Dictionary<int, ObjectPool<T[]>>? adapters = this.adapters;
            Debug.Assert(adapters is not null);
            SafeExactLengthArrayObjectPool<T> pool = new(length, default);
            adapters.Add(length, pool);

            int count = adaptersCount;
            SafeExactLengthArrayObjectPool<T>[] array = adaptersArray;
            if (unchecked((uint)count >= (uint)array.Length))
            {
                Array.Resize(ref array, array.Length * 2);
                adaptersArray = array;
            }
            array[count] = pool;
            adaptersCount = count + 1;

            return lastPool = pool;
        }
    }
}
