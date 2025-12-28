using System.Threading;

namespace Enderlook.Pools;

/// <summary>
/// Represent a pool of arrays of a given length.
/// </summary>
/// <remarks>The pool has an specified policy of clearing or not arrays on return which is specified at the moment of instantiating the pool, this is used for <see cref="ObjectPool{T}.Return(T)"/>.<br/>
/// However, for overriding such policy you can use the method <see cref="Return(T[], bool)"/>.</remarks>
/// <typeparam name="T">Type of element of the array.</typeparam>
public abstract class ArrayObjectPool<T> : ObjectPool<T[]>
{
    private protected ArrayObjectPool<T>? oppositeClear;

    /// <summary>
    /// Determines the default array clearing strategy.<br/>
    /// If this is <see langword="true"/>, buffers that will be stored to enable subsequent reuse in <see cref="Return(T[])"/>, will have their content cleared so that a subsequent consumer will not see the previous consumer's content.<br/>
    /// If <see langword="false"/> or if the pool will release the buffer, the array's contents are left unchanged.
    /// </summary>
    public abstract bool ShouldClearArrayOnReturnByDefault { get; }

    /// <inheritdoc cref="ObjectPool{T}.Return(T)"/>
    /// <param name="clearArray"> If <see langword="true"/> and if the pool will store the buffer to enable subsequent reuse, will clear the array of its contents so that a subsequent consumer will not see the previous consumer's content.<br/>
    /// If <see langword="false"/> or if the pool will release the buffer, the array's contents are left unchanged.</param>
    /// <remarks>The overload <see cref="ObjectPool{T}.Return(T)"/> uses a clearing policy specified by <see cref="ShouldClearArrayOnReturnByDefault"/>.</remarks>
    public abstract void Return(T[] element, bool clearArray);

    /// <inheritdoc cref="ObjectPool{T}.Return(T)"/>
    /// <remarks>It uses a clearing policy specified by <see cref="ShouldClearArrayOnReturnByDefault"/>.</remarks>
    public override void Return(T[] element) => Return(element, ShouldClearArrayOnReturnByDefault);

    /// <summary>
    /// Returns a instance of the pool in which the <see cref="ShouldClearArrayOnReturnByDefault"/> has been modified.
    /// </summary>
    /// <param name="clearArrayOnReturnByDefault">New value for <see cref="ShouldClearArrayOnReturnByDefault"/>.</param>
    /// <returns>An instance of the pool in which the <see cref="ShouldClearArrayOnReturnByDefault"/> has been modified.<br/>
    /// It may be a new instance, a pooled one or the same instance if the values matches.</returns>
    public virtual ArrayObjectPool<T> WithClearArrayOnReturn(bool clearArrayOnReturnByDefault)
    {
        if (ShouldClearArrayOnReturnByDefault == clearArrayOnReturnByDefault)
            return this;
        ArrayObjectPool<T>? pool = oppositeClear;
        return pool is not null ? pool : Work();
        ArrayObjectPool<T> Work()
        {
            SpecificClearArrayObjectPool<T> value = new(this, clearArrayOnReturnByDefault);
            return Interlocked.Exchange(ref oppositeClear, value) ?? value;
        }
    }
}

internal sealed class SpecificClearArrayObjectPool<T> : ArrayObjectPool<T>
{
    private readonly bool clearOnReturn;

    public override bool ShouldClearArrayOnReturnByDefault => clearOnReturn;

    public SpecificClearArrayObjectPool(ArrayObjectPool<T> pool, bool clearOnReturn)
    {
        oppositeClear = pool;
        this.clearOnReturn = clearOnReturn;
    }

    public override int ApproximateCount() => oppositeClear!.ApproximateCount();

    public override T[] Rent() => oppositeClear!.Rent();

    public override void Return(T[] element, bool clearArray) => oppositeClear!.Return(element, clearArray);

    public override void Trim(bool force = false) => oppositeClear!.Trim(force);
}
