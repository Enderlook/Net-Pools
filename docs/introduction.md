# Introduction

This library provides implementations of pools for .NET.

It provides implementations for both shared and individual pools:
 - Global pools that can be used across the entire application.
 - Individual or local pools can be instantiated for specific usages.

It also provides pools for both objects and arrays.
 - Object pools are for non-array types, including value-types (or `struct`), and in case the element provided implements `IDisposable`, it can execute `IDisposable.Dispose()` when the pool is trimmed on each freed element.
 - Unlike the `System.ArrayPool<T>`, the array pools of this library always returns arrays of the specified length. Arrays can be cleaned on return, but `IDisposable.Dispose()` is not executed on their elements.
 - Additionally, it also provides array pools that always rent and return the same length rather than specify it on each rent, it also always clear or not the returned arrays rather than specifying on each return, this allow to use them as simple object pools, but for renting arrays. Arrays can be cleaned on return, but `IDisposable.Dispose()` is not executed on their elements.

| Element | Global/Shared | Local/Individual |
|---|---|---|
| `T` | [`ObjectPool<T>.Shared`](xref:Enderlook.Pools.ObjectPool`1) | [`SafeObjectPool<T>`](xref:Enderlook.Pools.SafeObjectPool`1) <br/> [`FastObjectPool<T>`](xref:Enderlook.Pools.FastObjectPool`1)[^1] |
| `T[anyLength]` | [`ExactLengthArrayPool<T>.Shared`](xref:Enderlook.Pools.ExactLengthArrayPool`1) | [`SafeExactLengthArrayPool<T>`](xref:Enderlook.Pools.SafeExactLengthArrayPool`1) |
| `T[fixedLength` | [`ExactLengthArrayPool<T>.Shared.OfLength(int, bool)`](xref:Enderlook.Pools.ExactLengthArrayPool`1.OfLength(System.Int32,System.Boolean)) <br/> [`ExactLengthArrayPool<T>.SharedOfLength(int, bool)`](xref:Enderlook.Pools.ExactLengthArrayPool`1.SharedOfLength(System.Int32,System.Boolean))[^2] | [`SafeExactLengthArrayObjectPool<T>`](xref:Enderlook.Pools.SafeExactLengthArrayObjectPool`1) |

[^1]: [`FastObjectPool<T>`](xref:Enderlook.Pools.FastObjectPool`1) only supports `T` where it's a reference type, it also never calls the `IDisposable.Dispose()`, it's still thread-safe but during such scenarios some elements may be accidentally free rather than pool. However, it has the lowest overhead.
[^2]: [`ExactLengthArrayPool<T>.SharedOfLength(int, bool)`](xref:Enderlook.Pools.ExactLengthArrayPool`1.SharedOfLength(System.Int32,System.Boolean)) is a convenience method to acquire the shared specific-length pool without acquiring the non-specific-length first. In the end, both return the same instance.