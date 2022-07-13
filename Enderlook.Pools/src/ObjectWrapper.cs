namespace Enderlook.Pools;

internal struct ObjectWrapper<T> // Prevent runtime covariant checks on array access.
{
    public T Value;
}
