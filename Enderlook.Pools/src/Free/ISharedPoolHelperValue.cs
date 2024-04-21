namespace Enderlook.Pools.Free;

internal interface ISharedPoolHelperValue : ISharedPoolHelper
{
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
         abstract SharedThreadLocalElement NewLocal();
}