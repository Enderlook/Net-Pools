using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Enderlook.Pools;

internal sealed class GCCallbackObject<T>(ObjectPool<T> owner)
{
    private readonly GCHandle owner = GCHandle.Alloc(owner, GCHandleType.Weak);

    ~GCCallbackObject()
    {
        object? owner = this.owner.Target;
        if (owner is null)
            this.owner.Free();
        else
        {
            GC.ReRegisterForFinalize(this);
            Debug.Assert(owner is ObjectPool<T>);
            Unsafe.As<ObjectPool<T>>(owner).Trim();
        }
    }
}
