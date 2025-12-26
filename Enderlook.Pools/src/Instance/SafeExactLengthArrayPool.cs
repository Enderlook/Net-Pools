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
    /// Determines if the reserve pool is not to grow nor shrink given its usage.
    /// </summary>
    public bool IsReserveFixed { get; init; } = true;

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
        return OfLength(length, false).Rent();
#else
        return OfLength_(length, false).Rent();
#endif
    }

    /// <inheritdoc cref="ArrayPool{T}.Return(T[], bool)"/>
    public override void Return(T[] array, bool clearArray = false)
    {
        if (array is null) return;
#if NET5_0_OR_GREATER
        OfLength(array.Length, false).Return_(array, clearArray);
#else
        OfLength_(array.Length, false).Return_(array, clearArray);
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
    /// <param name="clearArrayOnReturn">If this is <see langword="true"/>, buffers that will be stored to enable subsequent reuse in <see cref="ObjectPool{T}.Return(T)"/>, will have their content cleared so that a subsequent consumer will not see the previous consumer's content.<br/>
    /// If <see langword="false"/> or if the pool will release the buffer, the array's contents are left unchanged.</param>
    /// <returns>Wrapper of pool.</returns>
#if NET5_0_OR_GREATER
    public override SafeExactLengthArrayObjectPool<T> OfLength(int length, bool clearArrayOnReturn = false)
#else
    public override ArrayObjectPool<T> OfLength(int length, bool clearArrayOnReturn = false) => OfLength_(length, clearArrayOnReturn);
    private SafeExactLengthArrayObjectPool<T> OfLength_(int length, bool clearArrayOnReturn)
#endif
    {
        SafeExactLengthArrayObjectPool<T>? pool = lastPool;
        if (pool is not null && pool.Length == length && pool.ShouldClearArrayOnReturnByDefault == clearArrayOnReturn)
            return pool;

        int key = clearArrayOnReturn ? -length - 1 : length;
        Dictionary<int, ObjectPool<T[]>>? adapters = this.adapters;
        if (adapters is null)
            return Fallback1();

        lock (adapters)
        {
            if (adapters.TryGetValue(key, out ObjectPool<T[]>? pool_))
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

            adapters.TryGetValue(clearArrayOnReturn ? length : -length - 1, out ObjectPool<T[]>? original);

            SafeExactLengthArrayObjectPool<T> pool = new(length, Unsafe.As<SafeExactLengthArrayObjectPool<T>?>(original), clearArrayOnReturn)
            {
                Capacity = Capacity,
                Reserve = Reserve,
                IsReserveFixed = IsReserveFixed
            };
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
