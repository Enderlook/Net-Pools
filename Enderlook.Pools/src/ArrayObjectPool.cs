namespace Enderlook.Pools;

/// <summary>
/// Represent a pool of arrays of a given length.
/// </summary>
/// <remarks>The pool has an specified policy of clearing or not arrays on return which is specified at the moment of instantiating the pool, this is used for <see cref="ObjectPool{T}.Return(T)"/>.<br/>
/// However, for overriding such policy you can use the method <see cref="Return(T[], bool)"/>.</remarks>
/// <typeparam name="T">Type of element of the array.</typeparam>
public abstract class ArrayObjectPool<T> : ObjectPool<T[]>
{
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
}
