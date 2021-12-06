using System.Runtime.CompilerServices;

namespace Enderlook.Pools
{
    /// <summary>
    /// Represent a pool of objects.
    /// </summary>
    /// <typeparam name="T">Type of object to pool</typeparam>
    public abstract class ObjectPool<T> where T : class
    {
        /// <summary>
        /// Retrieves a shared <see cref="ObjectPool{T}"/> instance.
        /// </summary>
        public static ObjectPool<T> Shared
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                // In some platforms such as Unity WebGL multithreading is not supported so we use a single threaded class
                // since we don't require syncronization of threads.
                if (Utilities.SupportMultithreading)
                    return ThreadLocalOverPerCoreLockedStacksObjectPool<T>.Singlenton;
                return SingleThreadDynamicObjectPool<T>.Singlenton;
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
        /// Rent an element from the pool.<br/>
        /// If the pool is empty, instantiate a new element.
        /// </summary>
        /// <returns>Rented element.</returns>
        public abstract T Rent();

        /// <summary>
        /// Return an element to the pool.<br/>
        /// If the pool is full, it's an implementation detail whenever the object is free or the pool is resized.<br/>
        /// If <paramref name="obj"/> is <see langword="null"/>, it's an implementation detail whenever it throws an exception or ignores the call.<br/>
        /// However, it should never fail silently.
        /// </summary>
        public abstract void Return(T obj);

        /// <summary>
        /// Trim the content of the pool.
        /// </summary>
        /// <param name="force">If <see langword="true"/>, the pool is forced to clear all elements inside. Otherwise, the pool may only clear partially or not clear at all if the heuristic says so.</param>
        public abstract void Trim(bool force = false);
    }
}
