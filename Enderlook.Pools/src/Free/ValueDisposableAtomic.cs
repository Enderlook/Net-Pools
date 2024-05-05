using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Enderlook.Pools.Free;

internal struct ValueDisposableAtomic<TElement> : ISharedPoolHelperValue
{
    public
#if NET7_0_OR_GREATER
        static
#endif
         bool HasLocalFinalizer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => throw new NotSupportedException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public
#if NET7_0_OR_GREATER
        static
#endif
         void Free(Array array, int startIndex, int endIndex, bool isArrayDiscarded)
    {
        Debug.Assert(array is TElement[]);
        TElement[] array_ = Unsafe.As<TElement[]>(array);
        Debug.Assert(endIndex >= startIndex);
        Debug.Assert(endIndex <= array_.Length);
        ref TElement start = ref Utils.GetArrayDataReference(array_);
        ref TElement current = ref Unsafe.Add(ref start, startIndex);
        ref TElement end = ref Unsafe.Add(ref start, endIndex);
        if (
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            RuntimeHelpers.IsReferenceOrContainsReferences<TElement>() &&
#endif
            !isArrayDiscarded)
        {
            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                Debug.Assert(current is IDisposable);
                ((IDisposable)current).Dispose();
                current = default!;
                current = ref Unsafe.Add(ref current, 1);
            }
        }
        else
        {
            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                Debug.Assert(current is IDisposable);
                ((IDisposable)current).Dispose();
                current = ref Unsafe.Add(ref current, 1);
            }
        }
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
        => throw new NotSupportedException();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public
#if NET7_0_OR_GREATER
        static
#endif
         void TryFree(SharedThreadLocalElement? threadLocal)
    {
        Debug.Assert(threadLocal is SharedThreadLocalElementDisposableAtomic<TElement>);
        SharedThreadLocalElementDisposableAtomic<TElement> slot = Unsafe.As<SharedThreadLocalElementDisposableAtomic<TElement>>(threadLocal);
        // Unlike renting or returning, we can't ignore locked thread locals here
        // This is because we alredy set to 0 the `MillisecondsTimeStamp`,
        // And because with bad luck and timing, that could make objects
        // potentially live forever.
        long value = Interlocked.Exchange(ref Unsafe.As<ValueAtom<TElement>, long>(ref slot.Value), default);
        ValueAtom<TElement> nullable = Unsafe.As<long, ValueAtom<TElement>>(ref value);
        if (nullable.NotSynchronizedHasValue)
        {
            Debug.Assert(nullable.NotSynchronizedValue is IDisposable);
            ((IDisposable)nullable.NotSynchronizedValue).Dispose();
        }
    }
}
