# Changelog

##
- Fix documentation typos.
- Fix accidental free of some objects when returned.
- Improve exception messages.
- Fix contention.

## v0.2.3
- Improve documentation.
- Fix documentation typos.
- Fix error when using value types without parameterless constructor.
- Reduce memory consumption of `DynamicValueObjectPool<T>`.
- Minimal performance improvements.
- Tweak exception message thrown when a public parameterless constructor is not found for reference types and no factory was provided.
- Fix trimming not preserving public parameterless constructors of types used in pools.
- Fix `ObjectPool<T>.Shared` throwing when `T` is a value type.

## v0.2.2
- Add support for trimming.

## v0.2.1
- Fix object references not being cleaned correctly in `ObjectPool<T>.Shared` where `T` is a reference type.
- Fix `ObjectPool<T>.Shared.Return(T obj)` storing twice the same object.
- Minimal performance improvements in `ObjectPool<T>.Shared` where `T` is an unmanaged value type.

## v0.2.0
- Remove `class` constraint in generic parameter `T` in `ObjectPool<T>`.
- Add `DynamicValueObjectPool<T>`. 
- Improve documentation of `ObjectPool<T>`.
- Rename parameter `obj` to `element` of `ObjectPool<T>.Return(T obj)`.

## v0.1.1
- Fix documentation
- Specify that `ObjectPool<T>.ApproximateCount()` must return `-1` if the operation is not supported.
- Specify that `ObjectPool<T>.Return(T obj)` must never fail silently if `obj` is `null`.
- Fix `DynamicObjectPool<T>` not auto-trimming.
- Fix `ThreadLocalOverPerLockedStacksObjectPool<T>.Rent()` (implementation behind `ObjectPool<T>.Shared.Rent()`) throwing when getting elements from global reserve.

## v0.1.0
Initial release.
