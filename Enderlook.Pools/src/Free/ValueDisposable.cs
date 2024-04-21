using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Enderlook.Pools.Free;

internal struct ValueDisposable<TElement> : ISharedPoolHelperValue
{
    public
#if NET7_0_OR_GREATER
        static
#endif
         bool HasLocalFinalizer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => true;
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
        => new SharedThreadLocalElementValueNonAtomicDisposable<TElement>();

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
        {
            TElement? value = slot.Value;
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TElement>())
#endif
                slot.Value = default;
            slot.Lock = SharedThreadLocalElementValueNonAtomic<TElement>.NOT_HAVE;
            Debug.Assert(value is IDisposable);
            ((IDisposable)value).Dispose();
        }
        else
        {
            slot.Lock = SharedThreadLocalElementValueNonAtomic<TElement>.NOT_HAVE;
        }
    }
}
