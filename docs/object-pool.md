# Using [`ObjectPool<T>`](xref:Enderlook.Pools.ObjectPool`1)

[`ObjectPool<T>`](xref:Enderlook.Pools.ObjectPool`1) is capable of being used for local pools rather than a shared one as described [here](shared-object-pool.html).

For this, there are two possible options.

## [`FastObjectPool<T>`](xref:Enderlook.Pools.FastObjectPool`1)

Represent a local pool that only supports reference types. This pool will never dispose freed objects and during multithreading access, it may freed some objects.

```cs
FastObjectPool<T> pool = new FastObjectPool<T>(() => CreateInstance());

T value = pool.Rent();
// [...]
pool.Return(value);
```

The pool support configurations about its capacity, reserve and if its internal reserve is capable of growing. Such configuration is provided with init-only properties.

To avoid passing a factory delegate, you can use the method [`FastObjectPool<T>.CrateDefault()`](xref:Enderlook.Pools.FastObjectPool.CreateDefault``1). Alternatively, you can pass an auto-generated default factory using [`ObjectPool<T>.Factory`](xref:Enderlook.Pools.ObjectPool.get_Factory``1)

```cs
FastObjectPool<T> pool = FastObjectPool<T>.CreateDefault();

T value = pool.Rent();
// [...]
pool.Return(value);
```

Or:

```cs
FastObjectPool<T> pool = new FastObjectPool<T>(ObjectPool<T>.Factory);

T value = pool.Rent();
// [...]
pool.Return(value);
```

## [`SafeObjectPool<T>`](xref:Enderlook.Pools.SafeObjectPool`1)

Unlike [`FastObjectPool<T>`](xref:Enderlook.Pools.FastObjectPool`1), this pool also supports value types, and will execute the `IDisposable.Dispose()` method of any element which implements it and is freed. This pool never accidentally frees objects during multihreading usage.

The usage of this pool is similar to the previous, but also supports a [`FreeCallback`](xref:Enderlook.Pools.SafeObjectPool`1.FreeCallback) init-only property that can be used to override the default disposing behaviour.

```cs
SafeObjectPool<T> pool = new SafeObjectPool<T>(() => CreateInstance())
{
    FreeCallback = e => e.CustomFree();
}

T value = pool.Rent();
// [...]
pool.Return(value);

pool.Trim(true); // Element gets free during trimming.
```