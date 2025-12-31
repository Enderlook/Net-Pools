# Using the [`ObjectPool<T>.Shared`](xref:Enderlook.Pools.ObjectPool.get_Shared``1)

For common object pooling there is no need to create a new pool. Instead the default global pool can be used, which is already thread-safe and supports auto-trimming.

The basic pattern is pairing a `Rent()` and `Return(T)` calls.

```cs
MyExpensiveType instance = ObjectPool<MyExpensiveType>.Shared.Rent();
// [...]
ObjectPool<MyExpensiveType>.Shared.Return(instance);

public class MyExpensiveType
{
    // [...]
}
```

However, the pool also supports a lease pattern, in which a handler for the value is returned and support disposing:

```cs
using Lease<MyExpensiveType> lease = ObjectPool<MyExpensiveType>.Shared.RentLease();
instance = lease.Value;
// [...]
```

Using the C#'s `using` pattern, `Dispose()` is automatically called at the end of the scope.

Take into account that for the sake of performance, the `Lease<T>` is an `struct` and lack of additional safety measures, that is, users should treat it with move semantics and ensure there is a single call to `Dispose()` per instance.

Additionally, pools are not constrained to only reference types, `T` may be a value type:

```cs
using Lease<MyExpensiveStruct> lease = ObjectPool<MyExpensiveStruct>.Shared.RentLease();
instance = lease.Value;
// [...]

public struct MyExpensiveStruct
{
    // [...]
}
```

This can be useful if the `struct` internally contains managed types or some costly value to produce.

Finally, the pool supports `IDisposable` in its elements, that is, during trimming (not during normal `Return`) it will check if pooled instance to free implements the interface, and if does, it will dipose them.

```cs
using (Lease<MyExpensiveType> lease = ObjectPool<MyExpensiveType>.Shared.RentLease())
{
    instance = lease.Value;
    // [...]
}
ObjectPool<MyExpensiveType>.Shared.Trim(true);

public class MyExpensiveType : IDisposable
{
    // [...]
}
```