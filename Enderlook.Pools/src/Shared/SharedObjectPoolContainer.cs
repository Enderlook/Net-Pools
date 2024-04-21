using Enderlook.Pools.Free;

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Enderlook.Pools;

internal static class SharedObjectPoolContainer<
#if NET5_0_OR_GREATER
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
#endif
T>
{
    public static readonly ObjectPool<T> Shared;

    static SharedObjectPoolContainer()
    {
        // If the type (may) requires disposing, we must use additional interlocking
        // in order to ensure we don't accidentally free objects not disposed,
        // or return disposed objects.

        // If the type is a value type, we require additional logic for exchange as it's not atomic,
        // And depending if it's managed, unmanaged or requires disposing, we must also use additional logics,
        // to clear GC references and ensure trimed elements are disposed correctly.

        // TODO: Add special support for atomic value types?

        if (typeof(T).IsValueType)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
#endif
            {
                if (typeof(IDisposable).IsAssignableFrom(typeof(T)))
                {
                    Shared = new SharedValuePool<T, ValueDisposable<T>>();
                }
                else
                {
                    Shared = new SharedValuePool<T, ManagedValueNotDisposable<T>>();
                }
            }
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            else
            {
                if (typeof(IDisposable).IsAssignableFrom(typeof(T)))
                {
                    if (Unsafe.SizeOf<ValueAtom<T>>() == sizeof(long))
                    {
                        Shared = new SharedValueAtomicDisposablePool<T>();
                    }
                    else
                    {
                        Shared = new SharedValuePool<T, ValueDisposable<T>>();
                    }
                }
                else
                {
                    Shared = new SharedNotDisposabledUnmanagedValuePool<T>();
                }
            }
#endif
        }
        else
        {
            if (typeof(IDisposable).IsAssignableFrom(typeof(T)))
            {
                Shared = new SharedReferencePool<T, ReferenceDisposable>();
            }
            else if (typeof(T).IsSealed)
            {
                Shared = new SharedReferencePool<T, ReferenceNotDisposable>();
            }
            else
            {
                Shared = new SharedReferencePool<T, ReferenceMayBeDisposable>();
            }
        }
        GCCallback<T> _ = new(Shared);
    }
}
