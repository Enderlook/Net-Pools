# Net-Pools
A pool implementation for Net objects.

```cs
MyExpensiveType instance = ObjectPool<MyExpensiveType>.Shared.Rent();
// [...]
ObjectPool<MyExpensiveType>.Shared.Return(instance);

public class MyExpensiveType
{
    // [...]
}
```

Unlike other pool implementations, this one also support value types:

```cs
MyExpensiveValueType instance = ObjectPool<MyExpensiveValueType>.Shared.Rent();
// [...]
ObjectPool<MyExpensiveValueType>.Shared.Return(instance);

public struct MyExpensiveValueType
{
    private AllocatedType value1;
    private AllocatedType value2;
    
    // Parameterless constructors in value types is a C# 10 feature.
    public MyExpensiveValueType()
    {
        value1 = new();
        value2 = new();
    }
    
    // [...]
}
```

Additionally, it provides a local (thread-safe) pool in case of not wanting to use the global one, which support both manual and automatic (GC-triggered) trimming.

```cs
// If delegate is not provided, the parameterless constructor is used instead.
ObjectPool<MyExpensiveType> pool = new DynamicObjectPool<MyExpensiveType>(() => CreateExpensiveType());
ObjectPool<MyExpensiveValueType> pool = new DynamicValueObjectPool<MyExpensiveValueType>();
```

# API

```cs
namespace Enderlook.Pools
{
    /// <summary>
    /// Represent a pool of objects.
    /// </summary>
    /// <typeparam name="T">Type of object to pool</typeparam>
    public abstract class ObjectPool<T>
    {
        /// <summary>
        /// Retrieves a shared <see cref="ObjectPool{T}"/> instance.
        /// </summary>
        public static ObjectPool<T> Shared { get; }

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
    
    /// <summary>
    /// A lightweight, fast, dynamically-sized and thread-safe object pool to store objects.<br/>
    /// The pool is designed for fast rent and return of elements, so during multithreading scenarios it may accidentally free unnecessary objects during return (however, this is not a fatal error).
    /// </summary>
    /// <typeparam name="T">Type of object to pool</typeparam>
    public sealed class DynamicObjectPool<T> : ObjectPool<T> where T : class
    {
        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        /// <param name="hotCapacity">Hot capacity of the pool.<br/>
        /// If this capacity is fulled, the pool will expand a cold region.<br/>
        /// The hot capacity should preferably be not greater than <c><see cref="Environment.ProcessorCount"/> * 2</c>.</param>
        /// <param name="initialColdCapacity">Initial capacity of the cold pool.<br/>
        /// This reserve pool is only acceded when the hot pool gets full or empty since it's slower.<br/>
        /// This pool has a dynamic size so this value represent the initial size of the pool which may enlarge or shrink over time.</param>
        /// <param name="factory">Delegate used to construct instances of the pooled objects.<br/>
        /// If no delegate is provided, a factory with the default constructor of <typeparamref name="T"/> will be used.</param>
        /// <exception cref="ArgumentOutOfRangeException">Throw when <paramref name="hotCapacity"/> is lower than 1.</exception>
        public DynamicObjectPool(int hotCapacity, int initialColdCapacity, Func<T>? factory);

        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        /// <param name="hotCapacity">Hot capacity of the pool.<br/>
        /// If this capacity is fulled, the pool will expand a cold region.<br/>
        /// The hot capacity should preferably be not greater than <c><see cref="Environment.ProcessorCount"/> * 2</c>.</param>
        /// <param name="factory">Delegate used to construct instances of the pooled objects.<br/>
        /// If no delegate is provided, a factory with the default constructor for <typeparamref name="T"/> will be used.</param>
        /// <exception cref="ArgumentOutOfRangeException">Throw when <paramref name="hotCapacity"/> is lower than 1.</exception>
        public DynamicObjectPool(int hotCapacity, Func<T>? factory);

        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        /// <param name="hotCapacity">Hot capacity of the pool. If this capacity is fulled, the pool will expand a cold region.</param>
        /// <exception cref="ArgumentOutOfRangeException">Throw when <paramref name="hotCapacity"/> is lower than 1.</exception>
        public DynamicObjectPool(int hotCapacity);

        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        /// <param name="factory">Delegate used to construct instances of the pooled objects.<br/>
        /// If no delegate is provided, a factory with the default constructor for <typeparamref name="T"/> will be used.</param>
        public DynamicObjectPool(Func<T>? factory);

        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        public DynamicObjectPool();
    }
    
    /// <summary>
    /// A lightweight, fast, dynamically-sized and thread-safe object pool to store value-type objects.<br/>
    /// The pool is designed for fast rent and return of elements, so during multithreading scenarios it may accidentally free unnecessary objects during return (however, this is not a fatal error).
    /// </summary>
    /// <typeparam name="T">Type of object to pool</typeparam>
    public sealed class DynamicValueObjectPool<T> : ObjectPool<T> where T : class
    {
        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        /// <param name="hotCapacity">Hot capacity of the pool.<br/>
        /// If this capacity is fulled, the pool will expand a cold region.<br/>
        /// The hot capacity should preferably be not greater than <c><see cref="Environment.ProcessorCount"/> * 2</c>.</param>
        /// <param name="initialColdCapacity">Initial capacity of the cold pool.<br/>
        /// This reserve pool is only acceded when the hot pool gets full or empty since it's slower.<br/>
        /// This pool has a dynamic size so this value represent the initial size of the pool which may enlarge or shrink over time.</param>
        /// <param name="factory">Delegate used to construct instances of the pooled objects.<br/>
        /// If no delegate is provided, a factory with the default constructor of <typeparamref name="T"/> will be used.</param>
        /// <exception cref="ArgumentOutOfRangeException">Throw when <paramref name="hotCapacity"/> is lower than 1.</exception>
        public DynamicValueObjectPool(int hotCapacity, int initialColdCapacity, Func<T>? factory);

        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        /// <param name="hotCapacity">Hot capacity of the pool.<br/>
        /// If this capacity is fulled, the pool will expand a cold region.<br/>
        /// The hot capacity should preferably be not greater than <c><see cref="Environment.ProcessorCount"/> * 2</c>.</param>
        /// <param name="factory">Delegate used to construct instances of the pooled objects.<br/>
        /// If no delegate is provided, a factory with the default constructor for <typeparamref name="T"/> will be used.</param>
        /// <exception cref="ArgumentOutOfRangeException">Throw when <paramref name="hotCapacity"/> is lower than 1.</exception>
        public DynamicValueObjectPool(int hotCapacity, Func<T>? factory);

        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        /// <param name="hotCapacity">Hot capacity of the pool. If this capacity is fulled, the pool will expand a cold region.</param>
        /// <exception cref="ArgumentOutOfRangeException">Throw when <paramref name="hotCapacity"/> is lower than 1.</exception>
        public DynamicValueObjectPool(int hotCapacity);

        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        /// <param name="factory">Delegate used to construct instances of the pooled objects.<br/>
        /// If no delegate is provided, a factory with the default constructor for <typeparamref name="T"/> will be used.</param>
        public DynamicValueObjectPool(Func<T>? factory);

        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        public DynamicValueObjectPool();
    }
}
```
