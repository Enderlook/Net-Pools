# Changelog

## v0.1.0
Initial release.

## v0.1.1
- Fix documentation
- Specify that `ObjectPool<T>.ApproximateCount()` must return `-1` if the operation is not supported.
- Specify that `ObjectPool<T>.Return(T obj)` must never fail silently if `obj` is `null`.
- Fix `DynamicObjectPool<T>` not auto-trimming.
- Fix `ThreadLocalOverPerLockedStacksObjectPool<T>.Rent()` (implementation behind `ObjectPool<T>.Shared.Rent()`) throwing when getting elements from global reserve.