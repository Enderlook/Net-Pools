# Net-Pools

A pool implementation for Net objects.

```cs
MyExpensiveType instance = ObjectPool<MyExpensiveType>.Shared.Rent();
// [...]
ObjectPool<MyExpensiveType>.Shared.Return(instance);

// Or alternatively:
using Lease<MyExpensiveType> lease = ObjectPool<MyExpensiveType>.Shared.RentLease();
instance = lease.Value;
// [...]

public class MyExpensiveType
{
    // [...]
}
```

Unlike other pool implementations, this one also supports value types:

```cs
MyExpensiveValueType instance = ObjectPool<MyExpensiveValueType>.Shared.Rent();
// [...]
ObjectPool<MyExpensiveValueType>.Shared.Return(instance);

// Or alternatively:
using Lease<MyExpensiveValueType> lease = ObjectPool<MyExpensiveValueType>.Shared.RentLease();
// [...]

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
// It supports to use a custom delegate or the parameterless constructor
ObjectPool<MyExpensiveType> pool = new SafeObjectPool<MyExpensiveType>(() => CreateExpensiveType());
ObjectPool<MyExpensiveValueType> pool = SafeObjectPool<MyExpensiveValueType>.CreateDefault(); // It's a separate method for better IL trimming support.
```

You can configure this local pools to have custom or default constructors of the instances, to execute specific logic (such as disposing) when trimming objects, or to disable resizing of the pool.

## API

```cs
namespace Enderlook.Pools
{
    /// <summary>
    /// Represent a pool of objects.
    /// </summary>
    /// <typeparam name="T">Type of object to pool</typeparam>
    public abstract class ObjectPool<T>
    {
        #region Extension members from ObjectPool. Has [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]

        /// <summary>
        /// Retrieves a shared <see cref="ObjectPool{T}"/> instance.
        /// </summary>
        public static ObjectPool<T> Shared { get; }

        /// <summary>
        /// Retrieves a factory method that creates new instances of <typeparamref name="T"/> using its parameterless constructor.<br/>
        /// If the parameterless constructor is not found, the delegate will instead throw when executed for a reference type, or use <see langword="default"/> for value type.
        /// </summary>
        /// <remarks>This is quite similar to <see cref="Activator.CreateInstance{T}()"/>, but attempts to be more performant.</remarks>
        public static Func<T> Factory { get; }

        /// <summary>
        /// Creates a new instance of <typeparamref name="T"/> using its parameterless constructor.<br/>
        /// If the parameterless constructor is not found, it will instead throw when executed for a reference type, or use <see langword="default"/> for value type.
        /// </summary>
        /// <remarks>This is quite similar to <see cref="Activator.CreateInstance{T}()"/>, but attempts to be more performant.</remarks>
        /// <returns>New instance of <typeparamref name="T"/>.</returns>
        /// <exception cref="MissingMethodException">Thrown when the <typeparamref name="T"/> is a reference type and has no public parameterless constructor.</exception>
        public static T CreateInstance();

        #endregion

        /// <summary>
        /// Gets an approximate count of the objects stored in the pool.<br/>
        /// This value is not accurate and may be lower or higher than the actual count.<br/>
        /// This is primarily used for debugging purposes.
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
        /// Rent an element from the pool.<br/>
        /// If the pool is empty, instantiate a new element.<br/>
        /// Implementors of this class can choose how elements are instantiated and initialized, or throw if instantiation of new elements is not supported.
        /// </summary>
        /// <returns>A contained of the rented element. When this container is disposed, the object will be returned to the pool.</returns>
        public Lease<T> RentLease();

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
    public sealed class FastObjectPool<T> : ObjectPool<T> where T : class
    {
        #region Extension members from FastObjectPool. Has [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]

        /// <summary>
        /// Creates a pool of objects.<br/>
        /// A factory with the parameterless constructor of <typeparamref name="T"/> will be provided.
        /// </summary>
        /// <returns>A new instance that manages pooled objects.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FastObjectPool<T> CreateDefault();

        #endregion

        /// <summary>
        /// Capacity of the pool.<br/>
        /// This region of the pool support concurrent access.<br/>
        /// The capacity should preferably be not greater than <c><see cref="Environment.ProcessorCount"/> * 2</c>, since it's fully iterated before accessing the reserve.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Throw when <see langword="value"/> is lower than 1.</exception>
        public int Capacity { get; init; } = Environment.ProcessorCount * 2 - 1;

        /// <summary>
        /// Current capacity of the reserve.<br/>
        /// This reserve pool is only acceded when the non-reserve capacity gets full or empty.<br/>
        /// This is because this region can only be acceded by a single thread<br/>
        /// This pool has a dynamic size so this value represent the initial size of the pool which may enlarge or shrink over time.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Throw when <see langword="value"/> is negative.</exception>
        public int Reserve { get; init; } = Environment.ProcessorCount * 2;

        /// <summary>
        /// Determines if the reserve pool is not allowed to grow nor shrink given its usage.
        /// </summary>
        public bool IsReserveFixed { get; init; } = true;

        /// <summary>
        /// Delegate which instantiates new objects.
        /// </summary>
        public Func<T> Factory { get; }

        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        /// <param name="factory">Delegate used to construct instances of the pooled objects.<br/>
        /// For a default delegate use <see cref="FastObjectPool.CreateDefault{T}"/> or <see cref="ObjectPool.get_Factory{T}"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is <see langword="null"/>.</exception>
        public FastObjectPool(Func<T> factory);
    }

    /// <summary>
    /// A fast, dynamically-sized and thread-safe object pool to store objects.<br/>
    /// This pool can be configured to automatically call <see cref="IDisposable"/> of elements that are free (for example during trimming, when pool is full or when the pool is disposed itself).
    /// </summary>
    /// <typeparam name="T">Type of object to pool</typeparam>
    public sealed class SafeObjectPool<T> : ObjectPool<T>
    {
        #region Extension members from SafeObjectPool. Has [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]

        /// <summary>
        /// Creates a pool of objects.<br/>
        /// A factory with the parameterless constructor of <typeparamref name="T"/> will be provided.
        /// </summary>
        /// <returns>A new instance that manages pooled objects.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SafeObjectPool<T> CreateDefault();

        #endregion

        /// <summary>
        /// Capacity of the pool.<br/>
        /// This region of the pool support concurrent access.<br/>
        /// The capacity should preferably be not greater than <c><see cref="Environment.ProcessorCount"/> * 2</c>, since it's fully iterated before accessing the reserve.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Throw when <see langword="value"/> is lower than 1.</exception>
        public int Capacity { get; init; } = Environment.ProcessorCount * 2 - 1;

        /// <summary>
        /// Current capacity of the reserve.<br/>
        /// This reserve pool is only acceded when the non-reserve capacity gets full or empty.<br/>
        /// This is because this region can only be acceded by a single thread<br/>
        /// This pool has a dynamic size so this value represent the initial size of the pool which may enlarge or shrink over time.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Throw when <see langword="value"/> is negative.</exception>
        public int Reserve { get; init; } = Environment.ProcessorCount * 2;

        /// <summary>
        /// Determines if the reserve pool is not allowed to grow nor shrink given its usage.
        /// </summary>
        public bool IsReserveFixed { get; init; } = true;

        /// <summary>
        /// If this value is not <see langword="null"/>, the callback will be executed on each element which is free from the pool.<br/>
        /// That is, it will be called in elements not being stored during <see cref="Return(T)"/>, or during elements free by <see cref="Trim(bool)"/> or its automatic cleaning.<br/>
        /// If no value is specified, by default it will include a callback which executes <see cref="IDisposable.Dispose"/> on elements which can be casted to <see cref="IDisposable"/>.
        /// </summary>
        /// <remarks>If no value is specified, by default it will include a callback, but we actually don't call it.<br/>
        /// Instead we run the behaviour inline. This is to avoid the delegate call.</remarks>
        public Action<T>? FreeCallback { get; init; }

        /// <summary>
        /// Delegate which instantiates new objects.
        /// </summary>
        public Func<T> Factory { get; }

        /// <summary>
        /// Creates a pool of objects.
        /// </summary>
        /// <param name="factory">Delegate used to construct instances of the pooled objects.<br/>
        /// For a default delegate use <see cref="FastObjectPool.CreateDefault{T}"/> or <see cref="ObjectPool.get_Factory{T}"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is <see langword="null"/>.</exception>
        public SafeObjectPool(Func<T> factory);
    }

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

        /// <summary>
        /// Rent an element from the pool.<br/>
        /// If the pool is empty, instantiate a new element.<br/>
        /// Implementors of this class can choose how elements are instantiated and initialized, or throw if instantiation of new elements is not supported.
        /// </summary>
        /// <param name="clearOnReturn"> If <see langword="true"/> and if the pool will store the buffer to enable subsequent reuse, will clear the array of its contents so that a subsequent consumer will not see the previous consumer's content.<br/>
        /// If <see langword="false"/> or if the pool will release the buffer, the array's contents are left unchanged.</param>
        /// <returns>A contained of the rented element. When this container is disposed, the object will be returned to the pool.</returns>
        public Lease<T[]> RentLease(bool clearOnReturn);

        /// <summary>
        /// Return an element to the pool.<br/>
        /// If the pool is full, it's an implementation detail whenever the object is free or the pool is resized.<br/>
        /// If <paramref name="element"/> is <see langword="default"/>, it's an implementation detail whenever it throws an exception or ignores the call. However, it should never leave the pool in an invalid state.
        /// </summary>
        /// <param name="element">Element to return.</param>
        /// <param name="clearArray"> If <see langword="true"/> and if the pool will store the buffer to enable subsequent reuse, will clear the array of its contents so that a subsequent consumer will not see the previous consumer's content.<br/>
        /// If <see langword="false"/> or if the pool will release the buffer, the array's contents are left unchanged.</param>
        /// <remarks>The overload <see cref="ObjectPool{T}.Return(T)"/> uses a clearing policy specified by <see cref="ShouldClearArrayOnReturnByDefault"/>.</remarks>
        public abstract void Return(T[] element, bool clearArray);

        /// <summary>
        /// Returns a instance of the pool in which the <see cref="ShouldClearArrayOnReturnByDefault"/> has been modified.
        /// </summary>
        /// <param name="clearArrayOnReturnByDefault">New value for <see cref="ShouldClearArrayOnReturnByDefault"/>.</param>
        /// <returns>An instance of the pool in which the <see cref="ShouldClearArrayOnReturnByDefault"/> has been modified.<br/>
        /// It may be a new instance, a pooled one or the same instance if the values matches.</returns>
        public virtual ArrayObjectPool<T> WithClearArrayOnReturn(bool clearArrayOnReturnByDefault);
    }

    /// <summary>
    /// A fast, dynamically-sized and thread-safe array pool to store arrays of an specific length.<br/>
    /// </summary>
    /// <typeparam name="T">Type of element array to pool</typeparam>
    public sealed class SafeExactLengthArrayObjectPool<T> : ArrayObjectPool<T>
    {
        /// <summary>
        /// Determines the length of the pooled arrays.
        /// </summary>
        public int Length { get; }

        /// <summary>
        /// Capacity of the pool.<br/>
        /// This region of the pool support concurrent access.<br/>
        /// The capacity should preferably be not greater than <c><see cref="Environment.ProcessorCount"/> * 2</c>, since it's fully iterated before accessing the reserve.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Throw when <see langword="value"/> is lower than 1.</exception>
        public int Capacity { get; init; } = Environment.ProcessorCount * 2 - 1;

        /// <summary>
        /// Current capacity of the reserve.<br/>
        /// This reserve pool is only acceded when the non-reserve capacity gets full or empty.<br/>
        /// This is because this region can only be acceded by a single thread<br/>
        /// This pool has a dynamic size so this value represent the initial size of the pool which may enlarge or shrink over time.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Throw when <see langword="value"/> is negative.</exception>
        public int Reserve { get; init; } = Environment.ProcessorCount * 2;

        /// <summary>
        /// Determines if the reserve pool is not allowed to grow nor shrink given its usage.
        /// </summary>
        public bool IsReserveFixed { get; init; } = true;

        /// <summary>
        /// Creates a pool of exact length array.
        /// </summary>
        /// <param name="length">Length of the pooled arrays.</param>
        /// <param name="shouldClearArrayOnReturnByDefault">If this is <see langword="true"/>, buffers that will be stored to enable subsequent reuse in <see cref="Return(T[])"/>, will have their content cleared so that a subsequent consumer will not see the previous consumer's content.<br/>
        /// If <see langword="false"/> or if the pool will release the buffer, the array's contents are left unchanged.</param>
        public SafeExactLengthArrayObjectPool(int length, bool shouldClearArrayOnReturnByDefault = false);

        /// <summary>
        /// Returns a instance of the pool in which the <see cref="ShouldClearArrayOnReturnByDefault"/> has been modified.
        /// </summary>
        /// <param name="clearArrayOnReturnByDefault">New value for <see cref="ShouldClearArrayOnReturnByDefault"/>.</param>
        /// <returns>An instance of the pool in which the <see cref="ShouldClearArrayOnReturnByDefault"/> has been modified.<br/>
        /// It may be a new instance, a pooled one or the same instance if the values matches.</returns>
        public SafeExactLengthArrayObjectPool<T> WithClearArrayOnReturn(bool clearArrayOnReturnByDefault);
    }

    /// <summary>
    /// Represent a pool of arrays of exact length.
    /// </summary>
    /// <typeparam name="T">Type of object to pool.</typeparam>
    public abstract class ExactLengthArrayPool<T> : ArrayPool<T>
    {
        /// <summary>
        /// Retrieves a shared <see cref="ExactLengthArrayPool{T}"/> instance.<br/>
        /// The shared pool has the following features:
        /// <list type="bullet">
        ///     <item>Instantiates new elements when empty.</item>
        ///     <item>Resize itself to accommodate all returned elements to the pool.</item>
        ///     <item>Periodically trims itself removing old elements from the pool (GC-triggered).</item>
        ///     <item>Is thread-safe.</item>
        /// </list>
        /// </summary>
        public static new ExactLengthArrayPool<T> Shared { get; }

        /// <summary>
        /// Rent an element from the pool.<br/>
        /// If the pool is empty, instantiate a new element.<br/>
        /// Implementors of this interface can choose how elements are instantiated and initialized, or throw if instantiation of new elements is not supported.
        /// </summary>
        /// <param name="minimumLength">The minimum length of the array needed.</param>
        /// <returns>A contained of the rented element. When this container is disposed, the object will be returned to the pool.</returns>
        public Lease<T[]> RentLease(int minimumLength);

        /// <summary>
        /// Rent an element from the pool.<br/>
        /// If the pool is empty, instantiate a new element.<br/>
        /// Implementors of this interface can choose how elements are instantiated and initialized, or throw if instantiation of new elements is not supported.
        /// </summary>
        /// <param name="minimumLength">The minimum length of the array needed.</param>
        /// <param name="clearOnReturn"> If <see langword="true"/> and if the pool will store the buffer to enable subsequent reuse, will clear the array of its contents so that a subsequent consumer will not see the previous consumer's content.<br/>
        /// If <see langword="false"/> or if the pool will release the buffer, the array's contents are left unchanged.</param>
        /// <returns>A contained of the rented element. When this container is disposed, the object will be returned to the pool.</returns>
        public Lease<T[]> RentLease(int minimumLength, bool clearOnReturn);

        /// <summary>
        /// Retrieves a shared <see cref="ObjectPool{T}"/> instance configured to create arrays of the specified length.<br/>
        /// The shared pool has the following features:
        /// <list type="bullet">
        ///     <item>Instantiates new elements when empty.</item>
        ///     <item>Resize itself to accommodate all returned elements to the pool.</item>
        ///     <item>Periodically trims itself removing old elements from the pool (GC-triggered).</item>
        ///     <item>Is thread-safe.</item>
        /// </list>
        /// </summary>
        /// <param name="length">Length of arrays.</param>
        /// <param name="clearArrayOnReturn"> If <see langword="true"/> and if the pool will store a buffer that is being returned to enable subsequent reuse, will clear the array of its contents so that a subsequent consumer will not see the previous consumer's content.<br/>
        /// If <see langword="false"/> or if the pool will release the buffer, the array's contents are left unchanged.</param>
        /// <returns>Wrapper of pool.</returns>
        public static ArrayObjectPool<T> SharedOfLength(int length, bool clearArrayOnReturn = false);

        /// <summary>
        /// Produces a wrapper <see cref="ObjectPool{T}"/> that uses the current instance to create arrays of the specified length.<br/>
        /// </summary>
        /// <param name="length">Length of arrays.</param>
        /// <param name="clearArrayOnReturn"> If <see langword="true"/> and if the pool will store a buffer that is being returned to enable subsequent reuse, will clear the array of its contents so that a subsequent consumer will not see the previous consumer's content.<br/>
        /// If <see langword="false"/> or if the pool will release the buffer, the array's contents are left unchanged.</param>
        /// <returns>Wrapper of pool.</returns>
        public virtual ArrayObjectPool<T> OfLength(int length, bool clearArrayOnReturn = false);

        /// <summary>
        /// Gets an approximate count of the objects stored in the pool.<br/>
        /// This value is not accurate and may be lower or higher than the actual count.<br/>
        /// This is primarily used for debugging purposes.
        /// </summary>
        /// <returns>Approximate count of elements in the pool. If this operation is not supported, return -1 instead of throwing.</returns>
        public abstract int ApproximateCount();

        /// <summary>
        /// Trim the content of the pool.
        /// </summary>
        /// <param name="force">If <see langword="true"/>, the pool is forced to clear all elements inside. Otherwise, the pool may only clear partially or not clear at all if the heuristic says so.</param>
        public abstract void Trim(bool force = false);
    }

    /// <summary>
    /// A fast, dynamically-sized and thread-safe array pool to store arrays of specific lengths.<br/>
    /// </summary>
    /// <typeparam name="T">Type of element array to pool</typeparam>
    public sealed class SafeExactLengthArrayPool<T> : ExactLengthArrayPool<T>
    {
        /// <summary>
        /// Capacity of the pool.<br/>
        /// This region of the pool support concurrent access.<br/>
        /// The capacity should preferably be not greater than <c><see cref="Environment.ProcessorCount"/> * 2</c>, since it's fully iterated before accessing the reserve.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Throw when <see langword="value"/> is lower than 1.</exception>
        public int Capacity { get; init; }

        /// <summary>
        /// Current capacity of the reserve.<br/>
        /// This reserve pool is only acceded when the non-reserve capacity gets full or empty.<br/>
        /// This is because this region can only be acceded by a single thread<br/>
        /// This pool has a dynamic size so this value represent the initial size of the pool which may enlarge or shrink over time.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Throw when <see langword="value"/> is negative.</exception>
        public int Reserve { get; init; }

        /// <summary>
        /// Determines if the reserve pool is not allowed to grow nor shrink given its usage.
        /// </summary>
        public bool IsReserveFixed { get; init; } = true;

        /// <summary>
        /// Creates a new instance of the pool.
        /// </summary>
        public SafeExactLengthArrayPool();

        /// <summary>
        /// Produces a wrapper <see cref="SafeExactLengthArrayObjectPool{T}"/> that uses the current instance to create arrays of the specified length.<br/>
        /// </summary>
        /// <param name="length">Length of arrays.</param>
        /// <param name="clearArrayOnReturn"> If <see langword="true"/> and if the pool will store a buffer that is being returned to enable subsequent reuse, will clear the array of its contents so that a subsequent consumer will not see the previous consumer's content.<br/>
        /// If <see langword="false"/> or if the pool will release the buffer, the array's contents are left unchanged.</param>
        /// <returns>Wrapper of pool.</returns>
        public override SafeExactLengthArrayObjectPool<T> OfLength(int length, bool clearArrayOnReturn = false);
    }

    /// <summary>
    /// Represent a pooled value.<br/>
    /// When <see cref="IDisposable.Dispose"/> is executed, the value is returned to the pool.
    /// </summary>
    /// <remarks>This type doesn't provide any double-dipose, default value or use after dispose safety.<br/>
    /// This is to ensure the highest performance of the type.<br/><br/>
    /// Users must ensure:
    /// <list type="bullet">
    /// <item>The instance is disposed exactly once.</item>
    /// <item>The field <see cref="Value"/> is not used after disposing.</item>
    /// <item>The instance is always obtained from a pool rather than using <see langword="default"/> (<c>default(<see cref="Lease{T}"/>)</c>) or calling its parameterless constructor.</item>
    /// <item>Treat the instance under move-semantics. This particularly important if <typeparamref name="T"/> is a mutable value type, as mutations in <see cref="Value"/> would differ between copies.</item>
    /// </list>
    /// </remarks>
    /// <typeparam name="T">Type of value to pool.</typeparam>
    public struct Lease<T> : IDisposable
    {
        /// <summary>
        /// Value that is being rented.
        /// </summary>
        /// <remarks>This is a field so when <c>T</c> is a mutable value type it can be mutated.<br/>
        /// However, it's not intended for users to replace the value with another one never.</remarks>
        public T Value;

        /// <summary>
        /// Returns the value to the pool.
        /// </summary>
        /// <remarks>This method must be executed exactly once per instance.</remarks>
        public readonly void Dispose();
    }
}
```
