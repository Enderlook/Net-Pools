namespace Enderlook.Pools;

/// <summary>
/// Represent an adapter of <see cref="ExactLengthArrayPool{T}"/> to <see cref="ArrayObjectPool{T}"/>.
/// </summary>
internal sealed class ExactLengthArrayPoolAdapter<TElement>(ExactLengthArrayPool<TElement> pool, int length, bool clearOnReturn) : ArrayObjectPool<TElement>
{
    /// <inheritdoc cref="ArrayObjectPool{T}.ShouldClearArrayOnReturnByDefault"/>
    public override bool ShouldClearArrayOnReturnByDefault => clearOnReturn;

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
        pool.Return(element!, clearOnReturn);
    }

    /// <inheritdoc cref="ArrayObjectPool{T}.Return(T[], bool)"/>
    public override void Return(TElement[] element, bool clearArray)
    {
        if (element?.Length != length)
            Utils.ThrowArgumentOutOfRangeException_ArrayLength();

        // We allow passing `null`s so they are handled by the actual implementation.
        pool.Return(element!, clearArray);
    }

    /// <inheritdoc cref="ObjectPool{T}.Trim(bool)"/>
    public override void Trim(bool force = false) => pool.Trim(force);

    /// <inheritdoc cref="ArrayObjectPool{T}.WithClearArrayOnReturn(bool)"/>
    public override ArrayObjectPool<TElement> WithClearArrayOnReturn(bool clearArrayOnReturnByDefault)
        => clearArrayOnReturnByDefault == clearOnReturn
            ? this
            : (oppositeClear ??= pool.OfLength(length, clearArrayOnReturnByDefault));
}