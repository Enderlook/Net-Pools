# Using the [`ExactLengthArrayPool<T>`](xref:Enderlook.Pools.ExactLengthArrayPool`1)

[`ExactLengthArrayPool<T>`](xref:Enderlook.Pools.ExactLengthArrayPool`1) is capable of being used for local pools rather than a shared one as described [here](shared-exact-length-array-pool.html).

For this you can use [`SafeExactLengthArrayPool<T>`](xref:Enderlook.Pools.SafeExactLengthArrayPool`1).

```cs
SafeExactLengthArrayPool<object> pool = new SafeExactLengthArrayPool<object>();

object[] array = pool.Rent(15);

// [...]

pool.Return(array, clearArray: true);
```

Additionally, if the arrays will always be of the same length, an specify pool does exist named [`SafeExactLengthArrayObjectPool<T>`](xref:Enderlook.Pools.SafeExactLengthArrayObjectPool`1).

```cs
SafeExactLengthArrayObjectPool<object> pool = new SafeExactLengthArrayObjectPool<object>(15, shouldClearArrayOnReturnByDefault: true);

object[] array = pool.Rent();

// [...]

pool.Return(array);
```

Note the constructor has a parameter named `shouldClearArrayOnReturnByDefault`, it's "`should`" because there is an overload of `Return` method to override such behaviour:

```cs
SafeExactLengthArrayObjectPool<object> pool = new SafeExactLengthArrayObjectPool<object>(15, shouldClearArrayOnReturnByDefault: true);

object[] array = pool.Rent();

// [...]

pool.Return(array, clearArray = false);
```

If the overload is not used, it will use the value specified in the constructor.

This pool inherits from [`ArrayObjectPool<T>`](xref:Enderlook.Pools.ArrayObjectPool`1) which derives from [`ObjectPool<T>`](xref:Enderlook.Pools.ObjectPool`1) and adds specific array functionality.

Additionally, is possible to adapt an existing pool to use a different clearing strategy:

```cs
SafeExactLengthArrayObjectPool<object> clear = new SafeExactLengthArrayObjectPool<object>(15, shouldClearArrayOnReturnByDefault: true);
SafeExactLengthArrayObjectPool<object> noClear = clear.WithClearArrayOnReturn(false);
```

Or to adapt a pool of multiple arrays to a single array length:

```cs
SafeExactLengthArrayPool<object> allPool = new SafeExactLengthArrayPool<object>();
SafeExactLengthArrayObjectPool<object> specificPool = allPool.OfLength(15, true);
```