# Using the [`ExactLengthArrayPool<T>.Shared`](xref:Enderlook.Pools.ExactLengthArrayPool`1.Shared)

For common array pooling the BCL provides `System.ArrayPool<T>.Shared`, however that pool provides arrays whose length is at least the one specified by the consumer.
For cases in which an array of an exact length is required, there is `ExactLengthArrayPool<T>.Shared`, which is a derived class of `ArrayPool<T>`, so it can be used in places where the former is used.

The basic pattern is pairing a `Rent(int length)` and `Return(T[], bool clearArray = false)` calls.

```cs
object[] instance = ExactLengthArrayPool<object>.Shared.Rent(12);
// [...]
ExactLengthArrayPool<object>.Shared.Return(instance);
```

However, the pool also supports a lease pattern, in which a handler for the value is returned and support disposing:

```cs
using Lease<object[]> lease = ExactLengthArrayPool<object[]>.Shared.RentLease();
instance = lease.Value;
// [...]
```

Using the C#'s `using` pattern, `Dispose()` is automatically called at the end of the scope.

Take into account that for the sake of performance, the `Lease<T>` is an `struct` and lack of additional safety measures, that is, users should treat it with move semantics and ensure there is a single call to `Dispose()` per instance.

Its also possible to acquire a shared pool of an specific array's length an treat it as a normal object pool using:

```cs
ObjectPool<object[]> pool = ExactLengthArrayPool<object>.Shared.OfLength(16, clearArrayOnReturn: false);

using Lease<object[]> lease = pool.Rent();
// [...]
```