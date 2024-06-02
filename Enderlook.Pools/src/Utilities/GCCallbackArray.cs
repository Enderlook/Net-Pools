using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Enderlook.Pools;

internal sealed class GCCallbackArray<T>(ExactLengthArrayPool<T> owner)
{
    private readonly GCHandle owner = GCHandle.Alloc(owner, GCHandleType.Weak);

    ~GCCallbackArray()
    {
        object? owner = this.owner.Target;
        if (owner is null)
            this.owner.Free();
        else
        {
            GC.ReRegisterForFinalize(this);
            Debug.Assert(owner is ExactLengthArrayPool<T>);
            Unsafe.As<ExactLengthArrayPool<T>>(owner).Trim();
        }
    }
}