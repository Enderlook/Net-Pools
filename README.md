# Net-Pools

[![Documentation](https://img.shields.io/badge/docs-DocFX-blue.svg)](https://enderlook.github.io/Net-Pools/docs/introduction.html)

A pool implementation for Net objects.

# Quickstart

Using the global pool is very straightforward.

```cs
MyExpensiveType instance = ObjectPool<MyExpensiveType>.Shared.Rent();

// [... execute some operations ...]

ObjectPool<MyExpensiveType>.Shared.Return(instance);

public class MyExpensiveType
{
    // [...]
}
```

It also supports the `using` pattern, value types and `IDisposable`:

```cs
using Lease<MyExpensiveType> lease = ObjectPool<MyExpensiveType>.Shared.RentLease();

instance = lease.Value;

// [... execute some operations ...]

public struct MyExpensiveType : IDisposable
{
    // [...]
}
```

And there are pools for arrays of an exact length rather than a minimum length:

```cs
using Lease<object[]> lease = ExactLengthArrayPool<object>.Shared.Rent(15);

// [... execute some operations ...]
````