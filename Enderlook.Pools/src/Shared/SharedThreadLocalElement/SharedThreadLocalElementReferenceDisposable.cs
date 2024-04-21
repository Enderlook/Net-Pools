using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Enderlook.Pools;

internal sealed class SharedThreadLocalElementReferenceDisposable : SharedThreadLocalElementReference
{
    ~SharedThreadLocalElementReferenceDisposable()
    {
        object? value = Value;
        if (value is not null)
        {
            Debug.Assert(value is IDisposable);
            Unsafe.As<IDisposable>(value).Dispose();
        }
    }
}
