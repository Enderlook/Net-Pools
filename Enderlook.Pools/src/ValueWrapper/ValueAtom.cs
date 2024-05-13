using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Enderlook.Pools;

#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
[StructLayout(LayoutKind.Sequential, Size = 8)]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
internal struct ValueAtom<T>(T value) : IValueWrapper<T>
{
    private bool has = true;
    private T value = value;

    public bool NotSynchronizedHasValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => has;
    }

    public T NotSynchronizedValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPopValue([NotNullWhen(true)] out T value)
    {
        long slot = Interlocked.Exchange(ref Unsafe.As<ValueAtom<T>, long>(ref this), default);
        ValueAtom<T> atom = Unsafe.As<long, ValueAtom<T>>(ref slot);
        value = atom.value;
        return atom.has;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TrySetValue(ref T value)
    {
        ValueAtom<T> atom = new(value);
        return Interlocked.CompareExchange(ref Unsafe.As<ValueAtom<T>, long>(ref this), Unsafe.As<ValueAtom<T>, long>(ref atom), default) == default;
    }
}
#endif