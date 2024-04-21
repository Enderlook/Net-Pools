using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Enderlook.Pools.Free;

internal struct ReferenceMayBeDisposable : ISharedPoolHelperReference
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
         object? Exchange(SharedThreadLocalElementReference threadLocal, object? element)
        => Interlocked.Exchange(ref threadLocal.Value, element);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public
#if NET7_0_OR_GREATER
        static
#endif
         void Free(Array array, int startIndex, int endIndex, bool isArrayDiscarded)
    {
        Debug.Assert(array is ObjectWrapper[]);
        ObjectWrapper[] array_ = Unsafe.As<ObjectWrapper[]>(array);
        Debug.Assert(endIndex >= startIndex);
        Debug.Assert(endIndex <= array_.Length);
        ref ObjectWrapper start = ref Utils.GetArrayDataReference(array_);
        ref ObjectWrapper current = ref Unsafe.Add(ref start, startIndex);
        ref ObjectWrapper end = ref Unsafe.Add(ref start, endIndex);
        if (isArrayDiscarded)
        {
            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                object? value = current.Value;
                current = ref Unsafe.Add(ref current, 1);
                if (value is IDisposable disposable)
                    disposable.Dispose();
            }
        }
        else
        {
            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                object? value = current.Value;
                current = default;
                current = ref Unsafe.Add(ref current, 1);
                if (value is IDisposable disposable)
                    disposable.Dispose();
            }
        }
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
        => new SharedThreadLocalElementReferenceMayBeDisposable();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public
#if NET7_0_OR_GREATER
        static
#endif
         object? Pop(SharedThreadLocalElementReference threadLocal)
        => threadLocal.Value is not null ? Interlocked.Exchange(ref threadLocal.Value, null) : null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public
#if NET7_0_OR_GREATER
        static
#endif
         void TryFree(SharedThreadLocalElement threadLocal)
    {
        Debug.Assert(threadLocal is SharedThreadLocalElementReference);
        SharedThreadLocalElementReference slot = Unsafe.As<SharedThreadLocalElementReference>(threadLocal);
        object? element = threadLocal;
        if (element is not null
            && ReferenceEquals(Interlocked.CompareExchange(ref slot.Value, null, element), element)
            && element is IDisposable disposable)
            disposable.Dispose();
    }
}