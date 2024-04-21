using System;

namespace Enderlook.Pools;

internal sealed class SharedThreadLocalElementReferenceMayBeDisposable : SharedThreadLocalElementReference
{
    ~SharedThreadLocalElementReferenceMayBeDisposable()
    {
        object? value = Value;
        if (value is IDisposable disposable)
            disposable.Dispose();
    }
}
