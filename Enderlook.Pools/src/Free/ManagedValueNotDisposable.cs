using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Enderlook.Pools.Free;

internal struct ManagedValueNotDisposable<TElement> : ISharedPoolHelperValue
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
         Array NewArray(int length) => new TElement[length];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public
#if NET7_0_OR_GREATER
        static
#endif
         SharedThreadLocalElement NewLocal()
        => new SharedThreadLocalElementValueNonAtomic<TElement>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public
#if NET7_0_OR_GREATER
        static
#endif
         void TryFree(SharedThreadLocalElement? threadLocal)
    {
        Debug.Assert(threadLocal is SharedThreadLocalElementValueNonAtomic<TElement>);
        SharedThreadLocalElementValueNonAtomic<TElement> slot = Unsafe.As<SharedThreadLocalElementValueNonAtomic<TElement>>(threadLocal);
        // Unlike renting or returning, we can't ignore locked thread locals here
        // This is because we alredy set to 0 the `MillisecondsTimeStamp`,
        // And because with bad luck and timing, that could make objects
        // potentially live forever.
        int @lock = Utils.MinusOneExchange(ref slot.Lock);
        if (@lock == SharedThreadLocalElementValueNonAtomic<TElement>.HAVE)
            slot.Value = default;
        slot.Lock = SharedThreadLocalElementValueNonAtomic<TElement>.NOT_HAVE;
    }
}