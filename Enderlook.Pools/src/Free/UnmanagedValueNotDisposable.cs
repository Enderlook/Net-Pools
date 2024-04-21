using System;
using System.Runtime.CompilerServices;

namespace Enderlook.Pools.Free;

internal struct UnmanagedValueNotDisposable<TElement> : ISharedPoolHelper
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public
#if NET7_0_OR_GREATER
        static
#endif
         void Free(Array array, int startIndex, int endIndex, bool isArrayDiscarded) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public
#if NET7_0_OR_GREATER
        static
#endif
         Array NewArray(int length) => new TElement[length];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public
#if NET7_0_OR_GREATER
        static
#endif
         void TryFree(SharedThreadLocalElement? threadLocal) => throw new NotSupportedException();
}