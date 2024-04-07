using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Enderlook.Pools;

internal struct ValueObjectWrapper<T> where T : struct
{
    // UNLOCK_EMPTY = 0;
    // LOCK = 1;
    // UNLOCK_FULL = 2;
    private T value;
    private int @lock;

    public bool NotSynchronizedHasValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => @lock == 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPopValue([NotNullWhen(true)] out T value)
    {
        int oldLock;
        while ((oldLock = Interlocked.Exchange(ref @lock, 1)) == 1) ;

        if (oldLock == 2)
        {
            value = this.value;
            this.value = default;
            @lock = 0;
            return true;
        }

#if NET5_0_OR_GREATER
        Unsafe.SkipInit(out value);
#else
        value = default;
#endif
        @lock = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TrySetValue(ref T value)
    {
        int oldLock;
        while ((oldLock = Interlocked.Exchange(ref @lock, 1)) == 1) ;

        if (oldLock == 2)
        {
            @lock = 2;
            return false;
        }

        this.value = value;
        @lock = 2;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        while (Interlocked.Exchange(ref @lock, 1) == 1) ;
        value = default;
        @lock = 0;
    }
}
