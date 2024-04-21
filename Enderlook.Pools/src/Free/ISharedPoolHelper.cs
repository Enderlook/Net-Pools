using System;

namespace Enderlook.Pools.Free;

internal interface ISharedPoolHelper
{
    // Free elements in the array at the specified location.
    // IF `isArrayDiscarded` is `true`, then the array won't be reused later, so you don't need to set GC references to null.
    public
#if NET7_0_OR_GREATER
        static
#endif
        abstract void Free(Array array, int startIndex, int count, bool isArrayDiscarded);

    // Allocates a new array.
    public
#if NET7_0_OR_GREATER
        static
#endif
         abstract Array NewArray(int length);

    // Try to free the element if it's not null.
    // The location must be set to null.
    public
#if NET7_0_OR_GREATER
        static
#endif
         abstract void TryFree(SharedThreadLocalElement threadLocal);
}
