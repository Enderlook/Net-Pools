namespace Enderlook.Pools;

/// <summary>
/// Represent an adapter of <see cref="ExactLengthArrayPool{T}"/> to <see cref="ObjectPool{T}"/>.
/// </summary>
/// <typeparam name="TElement"></typeparam>
internal sealed class ExactLengthArrayPoolAdapter<TElement>(ExactLengthArrayPool<TElement> pool, int length) : ObjectPool<TElement[]>
{
    /// <inheritdoc cref="ObjectPool{T}.ApproximateCount"/>
    public override int ApproximateCount() => pool.ApproximateCount();

    /// <inheritdoc cref="ObjectPool{T}.Rent"/>
    public override TElement[] Rent() => pool.Rent(length);

    /// <inheritdoc cref="ObjectPool{T}.Return(T)"/>
    public override void Return(TElement[] element)
    {
        if (element?.Length != length)
            Utils.ThrowArgumentOutOfRangeException_ArrayLength();

        // We allow passing `null`s so they are handled by the actual implementation.
        pool.Return(element!);
    }

    /// <inheritdoc cref="ObjectPool{T}.Trim(bool)"/>
    public override void Trim(bool force = false) => pool.Trim(force);
}