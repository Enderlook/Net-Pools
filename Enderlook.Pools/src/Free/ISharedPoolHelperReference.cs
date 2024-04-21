namespace Enderlook.Pools.Free;

internal interface ISharedPoolHelperReference : ISharedPoolHelper
{
    // Exchange the value at the specified location.
    public
#if NET7_0_OR_GREATER
        static
#endif
         abstract object? Exchange(SharedThreadLocalElementReference threadLocal, object? element);

    // Determines if the thread local has a finalizer or not.
    public
#if NET7_0_OR_GREATER
        static
#endif
         abstract bool HasLocalFinalizer { get; }

    // Creates a new thread local.
    public
#if NET7_0_OR_GREATER
        static
#endif
         abstract SharedThreadLocalElementReference NewLocal();

    public
#if NET7_0_OR_GREATER
        static
#endif
         abstract object? Pop(SharedThreadLocalElementReference threadLocal);
}