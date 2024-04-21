using System;
using System.Diagnostics;

namespace Enderlook.Pools;

internal sealed class SharedThreadLocalElementValueNonAtomicDisposable<T> : SharedThreadLocalElementValueNonAtomic<T>
{
    ~SharedThreadLocalElementValueNonAtomicDisposable()
    {
        int @lock = Lock;
        Debug.Assert(@lock != LOCKED);
        if (@lock == HAVE)
            ((IDisposable)Value!).Dispose();
    }
}