using System.Runtime.CompilerServices;

namespace Enderlook.Pools;

/// <summary>
/// Represent a pool of objects.
/// </summary>
/// <typeparam name="T">Type of object to pool.</typeparam>
public abstract class ObjectPool<T>
{
    /// <summary>
    /// Retrieves a shared <see cref="ObjectPool{T}"/> instance.<br/>
    /// The shared pool has the following features:
    /// <list type="bullet">
    ///     <item>Instantiates new elements when empty using the parameterless constructor (or <see langword="default"/> for value types if missing) of <typeparamref name="T"/>.</item>
    ///     <item>Resize itself to accommodate all returned elements to the pool.</item>
    ///     <item>Periodically trims itself removing old elements from the pool (GC-triggered).</item>
    ///     <item>Is thread-safe.</item>
    /// </list>
    /// </summary>
    public static ObjectPool<T> Shared
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => SharedObjectPoolContainer<T>.Shared;
    }

    /// <summary>
    /// Gets an approximate count of the objects stored in the pool.<br/>
    /// This value is not accurate and may be lower or higher than the actual count.<br/>
    /// This is primary used for debugging purposes.
    /// </summary>
    /// <returns>Approximate count of elements in the pool. If this operation is not supported, return -1 instead of throwing.</returns>
    public abstract int ApproximateCount();

    /// <summary>
    /// Rent an element from the pool.<br/>
    /// If the pool is empty, instantiate a new element.<br/>
    /// Implementors of this interface can choose how elements are instantiated and initialized, or throw if instantiation of new elements is not supported.
    /// </summary>
    /// <returns>Rented element.</returns>
    public abstract T Rent();

    /// <summary>
    /// Return an element to the pool.<br/>
    /// If the pool is full, it's an implementation detail whenever the object is free or the pool is resized.<br/>
    /// If <paramref name="element"/> is <see langword="default"/>, it's an implementation detail whenever it throws an exception or ignores the call. However, it should never leave the pool in an invalid state.
    /// </summary>
    /// <param name="element">Element to return.</param>
    public abstract void Return(T element);

    /// <summary>
    /// Trim the content of the pool.
    /// </summary>
    /// <param name="force">If <see langword="true"/>, the pool is forced to clear all elements inside. Otherwise, the pool may only clear partially or not clear at all if the heuristic says so.</param>
    public abstract void Trim(bool force = false);
}