namespace Enderlook.Pools;

internal class SharedThreadLocalElementValueNonAtomic<T> : SharedThreadLocalElement
{
    public const int NOT_HAVE = 0;
    public const int HAVE = 1;
    public const int LOCKED = Utils.LOCKED;

    public int Lock;
    public T? Value;
}