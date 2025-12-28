# Changelog

## WIP

- Rework array pools to support configuring clearing policy at its construction:
  - Create `ArrayObjectPool<T>` and make `SafeExactLengthArrayObjectPool<T>` inherit from it.
  - Modify constructor `SafeExactLengthArrayObjectPool(int length)` to `SafeExactLengthArrayObjectPool(int length, bool shouldClearArrayOnReturnByDefault = false)`.
  - Modify `ObjectPool<T[]> ExactLengthArrayPool<T>.OfLength(int length)` to `ArrayObjectPool<T> ExactLengthArrayPool<T>.OfLength(int length, bool clearArrayOnReturn = false)`.
- Fix typo in documentation.
- Fix error in `SafeExactLengthArrayPool<T>` and `SafeExactLengthArrayObjectPool<T>` produced when using the reserve and `T` was a value type.

## 0.4.0

- Reduce generic instantiations internally produced by `ExactLengthArrayPool<T>.Shared` and `ExactLengthArrayPool<T>.SharedOfLength(int)`.
- Fix error in `ExactLengthArrayPool<T>.Shared.ApproximateCount()` and `ExactLengthArrayPool<T>.Shared.Trim(bool)`.
- Fix `ExactLengthArrayPool<T>.Shared` and `ExactLengthArrayPool<T>.SharedOfLength(int)` returned incorrect array lengths.
- Improve caching performance in `ExactLengthArrayPool<T>.Shared` and `ExactLengthArrayPool<T>.SharedOfLength(int)` for common lengths.
- Avoid contention when calling `FastObjectPool<T>.Reserve` and `SafeObjectPool<T>.Reserve`.
- Replace `IsReserveDynamic` with `IsReserveFixed` in `FastObjectPool<T>`, `SafeObjectPool<T>`, `SafeExactLengthArrayObjectPool<T>` and `SafeExactLengthArrayPool<T>`.
- Fix `SafeExactLengthArrayPool<T>` creating non-configured instances of `SafeExactLengthArrayObjectPool<T>`.
- Improve trimming support by correctly annotating and modifying APIs:
  - Remove nullability in `new FastObjectPool<T>(Func<T>?)` and `new SafeObjectPool<T>(Func<T>?)`.
  - Remove parameterless constructor `new FastObjectPool<T>()` and `new FastObjectPool<T>()`.
  - Remove `ObjectPool<T>.Shared`.
  - Add `ObjectPool` class, which includes extension members for `ObjectPool<T>` and is correctly annotated to require dynamic access to the public parameterless constructor:
    - `ObjectPool<T>.Shared`.
    - `ObjectPool<T>.Factory`.
    - `ObjectPool<T>.CreateInstance()`.
  - Add `FastObjectPool` and `SafeObjectPool` classes, which includes extension members for `FastObjectPool<T>` and `SafeObjectPool<T>`, and are correctly annotated to require dynamic access to the public parameterless constructor:
    - `FastObjectPool<T>.CreateDefault()`.
    - `SafeObjectPool<T>.CreateDefault()`.

## v0.3.1

- Fix accidental double allocation in `ExactLengthArrayPool<T>.OfLength(int)`.
- Fix accidental allocation in `ExactLengthArrayPool<T>.SharedOfLength(int)` and methods `Rent()` and `Return()` of that returned value.
- Micro optimization by trying to avoid contention when doing `ObjectPool<T>.Shared.Rent()`.
- Optimize `ExactLengthArrayPool<T>.SharedOfLength(int)` and methods `Rent()` and `Return()` of that returned value when using consecutive repeated same length.

## v0.3.0

- Fix documentation typos.
- Fix accidental free of some objects when returned.
- Improve exception messages.
- Fix contention.
- Micro optimization during first call of `ObjectPool<T>.Shared.Return(T)` in an specific thread.
- Micro optimization during automatic trimming of `ObjectPool<T>.Shared`.
- Support `Trim(bool)` in `ObjectPool<T>.Shared`.
- Fix `ObjectPool<T>.Object.Rent()` not working when `T` is a value type.
- Reduce IL size by deduplicating classes.
- Reduce generic instantiations when using code.
- Fix possible racing condition in `ObjectPool<T>.Shared` where `T` is a value type.
- Rename `DynamicObjectPool<T>` to `FastObjectPool<T>` and `DynamicValueObjectPool<T>` to `SafeObjectPool<T>` and remove `where T : struct` constraint.
- Add `SafeObjectPool<T>`.
- Support pooling default elements in `SafeValueObjectPool<T>`.
- Replace constructors in `FastObjectPool<T>` and `SafeValueObjectPool<T>` and add init-only properties to configure it.
- Support configuring if its reserve (previously known as cold capacity) can be resized in `FastObjectPool<T>` and `SafeValueObjectPool<T>`.
- Support disposing and custom disposing in `SafeValueObjectPool<T>`.
- Fix `Trim(bool)` in `ObjectPool<T>.Shared` not trimming correctly values.
- Support disposing in `ObjectPool<T>.Shared`.
- Add `ExactLengthArrayPool<T>`, `SafeExactLengthArrayPoool<T>` and `SafeExactLengthArrayObjectPool<T>`.

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
