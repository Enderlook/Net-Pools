# Changelog

## v0.1.0
Initial release.

##
- Fix documentation
- Specify that `ObjectPool<T>.ApproximateCount()` must return `-1` if the operation is not supported.
- Specify that `ObjectPool<T>.Return(T obj)` must never fail silently if `obj` is `null`.