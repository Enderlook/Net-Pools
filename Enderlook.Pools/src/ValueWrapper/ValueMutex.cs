using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Enderlook.Pools;

internal struct ValueMutex<T> : IValueWrapper<T>
{
    // UNLOCK_EMPTY = 0;
    // LOCK = 1;
    // UNLOCK_FULL = 2;
    public T? NotSynchronizedValue;
    private int @lock;

    public bool NotSynchronizedHasValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => @lock == 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPopValue([NotNullWhen(true)] out T value)
    {
        int oldLock = Lock();

        if (oldLock == 2)
        {
            value = NotSynchronizedValue!;
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
#endif
                NotSynchronizedValue = default;
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
        int oldLock = Lock();

        if (oldLock == 2)
        {
            @lock = 2;
            return false;
        }

        this.NotSynchronizedValue = value;
        @lock = 2;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        Lock();
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
#endif
            NotSynchronizedValue = default;
        @lock = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Lock()
    {
        SpinWait spinWait = new();
        int oldLock = @lock;
        while (true)
        {
            if (oldLock != 1)
            {
                oldLock = Interlocked.Exchange(ref @lock, 1);
                if (oldLock != 1)
                    break;
            }
            spinWait.SpinOnce();
            oldLock = @lock;
        }
        return oldLock;
    }
}
