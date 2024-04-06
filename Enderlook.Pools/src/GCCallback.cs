using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Enderlook.Pools;

internal sealed class GCCallback<T>
{
    private readonly GCHandle owner;

    public GCCallback(ObjectPool<T> owner) => this.owner = GCHandle.Alloc(owner, GCHandleType.Weak);

    ~GCCallback()
    {
        object? owner = this.owner.Target;
        if (owner is null)
            this.owner.Free();
        else
        {
            Debug.Assert(owner is ObjectPool<T>);
            Unsafe.As<ObjectPool<T>>(owner).Trim();
            GC.ReRegisterForFinalize(this);
        }
    }
}