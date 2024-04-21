using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Enderlook.Pools.Free;

internal struct ReferenceNotDisposable : ISharedPoolHelperReference
{
    public
#if NET7_0_OR_GREATER
        static
#endif
         bool HasLocalFinalizer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public
#if NET7_0_OR_GREATER
        static
#endif
         object? Exchange(SharedThreadLocalElementReference threadLocal, object? element)
    {
        object? old = threadLocal.Value;
        threadLocal.Value = element;
        return old;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public
#if NET7_0_OR_GREATER
        static
#endif
         void Free(Array array, int startIndex, int endIndex, bool isArrayDiscarded)
    {
        if (!isArrayDiscarded)
            Array.Clear(array, startIndex, endIndex - startIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public
#if NET7_0_OR_GREATER
        static
#endif
         Array NewArray(int length) => new ObjectWrapper[length];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public
#if NET7_0_OR_GREATER
        static
#endif
         SharedThreadLocalElementReference NewLocal()
        => new SharedThreadLocalElementReference();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public
#if NET7_0_OR_GREATER
        static
#endif
         object? Pop(SharedThreadLocalElementReference threadLocal)
    {
        object? obj = threadLocal.Value;
        threadLocal.Value = null;
        return obj;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public
#if NET7_0_OR_GREATER
        static
#endif
         void TryFree(SharedThreadLocalElement threadLocal)
    {
        Debug.Assert(threadLocal is SharedThreadLocalElementReference);
        SharedThreadLocalElementReference slot = Unsafe.As<SharedThreadLocalElementReference>(threadLocal);
        slot.Value = null;
    }
}