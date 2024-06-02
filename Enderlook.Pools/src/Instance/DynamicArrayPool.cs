using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Enderlook.Pools;

/// <summary>
/// A lightweight, fast, dynamically-sized and thread-safe fixed-length array pool to store objects.<br/>
/// The pool is designed for fast rent and return of arrays, so during multithreading scenarios it may accidentally free unnecessary objects during return (however, this is not a fatal error).
/// </summary>
/// <typeparam name="T">Type of object to pool</typeparam>
public sealed class DynamicArrayPool<T> : ExactLengthArrayPool<T>
{
    private readonly Dictionary<int, FixedArrayPool<T>> poolsMap = [];
    private int @lock;
    private FixedArrayPool<T>[] poolsArray = [];
    private int poolsArrayCount;

    private int capacity;
    private int reserve;

    /// <summary>
    /// Capacity of the pool.<br/>
    /// This region of the pool support concurrent access.<br/>
    /// The capacity should preferably be not greater than <c><see cref="Environment.ProcessorCount"/> * 2</c>, since it's fully iterated before accessing the reserve.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Throw when <see langword="value"/> is lower than 1.</exception>
    public int Capacity
    {
        get => capacity;
        init
        {
            if (value < 1) Utils.ThrowArgumentOutOfRangeException_CapacityCanNotBeLowerThanOne();
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
    /// Creates a pool of arrays.
    /// </summary>
    public DynamicArrayPool()
    {
        capacity = reserve = Environment.ProcessorCount * 2;
        GCCallbackArray<T> _ = new(this);
    }

    /// <summary>
    /// Return a pool for a fixed length array.
    /// </summary>
    /// <param name="length">Length of the array.</param>
    /// <returns>A fixed-length array pool.</returns>
    public FixedArrayPool<T> GetPoolOf(int length) => GetPoolOf_(length);

    /// <summary>
    /// Rent an array from the pool.
    /// </summary>
    /// <param name="length">Length of the array to pool</param>
    /// <returns>Rented array.</returns>
    public override T[] Rent(int length) => GetPoolOf_(length).Rent_();

    /// <summary>
    /// Return an array to the pool.
    /// </summary>
    /// <param name="array">Array to return to the pool.</param>
    /// <param name="clearArray">Determines if the array must be clean.</param>
    public override void Return(T[] array, bool clearArray = false)
    {
        if (array is null)
            return;
        GetPoolOf_(array.Length).Return_(array);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private FixedArrayPool<T> GetPoolOf_(int length)
    {
        // TODO: Test if this would be more performant with a read/write lock.

        Utils.MinusOneExchange(ref @lock);
        Dictionary<int, FixedArrayPool<T>> pool = poolsMap;
        if (pool.TryGetValue(length, out FixedArrayPool<T>? value))
        {
            @lock = 0;
            return value;
        }
        return SlowPath(length, pool);

        [MethodImpl(MethodImplOptions.NoInlining)]
        FixedArrayPool<T> SlowPath(int length, Dictionary<int, FixedArrayPool<T>> pool)
        {
            FixedArrayPool<T>? value = new(length, false)
            {
                Capacity = capacity,
                Reserve = reserve,
                IsReserveDynamic = IsReserveDynamic,
            };
            pool.Add(length, value);

            int poolsArrayCount = this.poolsArrayCount;
            FixedArrayPool<T>[] poolsArray = this.poolsArray;
            int poolsArrayLength = poolsArray.Length;
            if (poolsArrayCount >= poolsArrayLength)
            {
                Array.Resize(ref poolsArray, Math.Max(16, poolsArrayLength * 2));
                this.poolsArray = poolsArray;
            }
            poolsArray[poolsArrayCount] = value;
            this.poolsArrayCount = poolsArrayCount + 1;

            @lock = 0;
            return value;
        }
    }

    /// <inheritdoc cref="ExactLengthArrayPool{T}.ApproximateCount"/>
    public override int ApproximateCount()
    {
        int count = poolsArrayCount;
        FixedArrayPool<T>[] array = poolsArray;
        Debug.Assert(count < array.Length);
        ref FixedArrayPool<T> current = ref Utils.GetArrayDataReference(array);
        ref FixedArrayPool<T> end = ref Unsafe.Add(ref current, count);
        int total = 0;
        while (Unsafe.IsAddressLessThan(ref current, ref end))
        {
            total += current.ApproximateCount();
            current = ref Unsafe.Add(ref current, 1);
        }
        return total;
    }

    /// <inheritdoc cref="ExactLengthArrayPool{T}.Trim(bool)"/>
    public override void Trim(bool force = false)
    {
        int count = poolsArrayCount;
        FixedArrayPool<T>[] array = poolsArray;
        Debug.Assert(count < array.Length);
        ref FixedArrayPool<T> current = ref Utils.GetArrayDataReference(array);
        ref FixedArrayPool<T> end = ref Unsafe.Add(ref current, count);
        while (Unsafe.IsAddressLessThan(ref current, ref end))
        {
            current.Trim(force);
            current = ref Unsafe.Add(ref current, 1);
        }
    }
}
