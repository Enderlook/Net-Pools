using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Enderlook.Pools;

internal sealed class SharedExactLengthArrayPool<T> : ExactLengthArrayPool<T>
{
    public static readonly SharedExactLengthArrayPool<T> Shared_ = new();

    private SharedExactLengthArrayPool()
    {
        GCCallbackArray<T> _ = new(this);
    }

    /// <inheritdoc cref="ExactLengthArrayPool{T}.ApproximateCount"/>
    public override int ApproximateCount()
    {
        ReadOnlySpan<SharedExactLengthArrayObjectPool<T>> span = SharedExactLengthArrayObjectPool<T>.Pools;
        ref SharedExactLengthArrayObjectPool<T> current = ref MemoryMarshal.GetReference(span);
        ref SharedExactLengthArrayObjectPool<T> end = ref Unsafe.Add(ref current, span.Length);
        int count = 0;
        while (Unsafe.IsAddressLessThan(ref current, ref end))
        {
            count += current.ApproximateCount();
            current = ref Unsafe.Add(ref end, 1);
        }
        return count;
    }

    /// <inheritdoc cref="ExactLengthArrayPool{T}.OfLength(int)"/>
    public override ObjectPool<T[]> OfLength(int length) => SharedExactLengthArrayObjectPool<T>.GetPool(length);

    /// <summary>
    /// Rents an array of the specified length.
    /// </summary>
    /// <param name="length">Length of the array.</param>
    /// <returns>Rented array.</returns>
    public override T[] Rent(int length) => SharedExactLengthArrayObjectPool<T>.Rent_(length);

    /// <summary>
    /// Returns an array to the pool. 
    /// </summary>
    /// <param name="array">Array to return.</param>
    /// <param name="clearArray">Indicates whether the contents of the array should be cleared before resue.<br/>
    /// If <see langword="true"/>, the array will be cleaned if the array ends stored in the pool.</param>
    public override void Return(T[] array, bool clearArray = false)
    {
        if (array is null) Utils.ThrowArgumentNullException_Array();
        if (clearArray)
            Array.Clear(array, 0, array.Length);
        SharedExactLengthArrayObjectPool<T>.Return_(array);
    }

    /// <inheritdoc cref="ExactLengthArrayPool{T}.Trim(bool)"/>
    public override void Trim(bool force = false)
    {
        ReadOnlySpan<SharedExactLengthArrayObjectPool<T>> span = SharedExactLengthArrayObjectPool<T>.Pools;
        ref SharedExactLengthArrayObjectPool<T> current = ref MemoryMarshal.GetReference(span);
        ref SharedExactLengthArrayObjectPool<T> end = ref Unsafe.Add(ref current, span.Length);
        while (Unsafe.IsAddressLessThan(ref current, ref end))
        {
            current.Trim(force);
            current = ref Unsafe.Add(ref end, 1);
        }
    }
}
