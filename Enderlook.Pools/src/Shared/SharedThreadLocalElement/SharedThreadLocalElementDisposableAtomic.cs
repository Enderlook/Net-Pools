#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Enderlook.Pools;

internal class SharedThreadLocalElementDisposableAtomic<T> : SharedThreadLocalElement
{
    public ValueAtom<T> Value;

#if DEBUG
    static SharedThreadLocalElementDisposableAtomic()
    {
        Debug.Assert(Unsafe.SizeOf<ValueAtom<T>>() == sizeof(long));
    }
#endif

    ~SharedThreadLocalElementDisposableAtomic()
    {
        if (Value.Has)
        {
            ((IDisposable)Value.Value!).Dispose();
        }
    }
}
#endif