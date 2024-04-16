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
        if (!typeof(T).IsValueType)
            Shared = new SharedObjectPool<T, SharedThreadLocalElement, ObjectWrapper>();
#if NET5_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        else if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
#endif
            Shared = new SharedObjectPool<T, SharedThreadLocalElement, T>();
#if NET5_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        else
            Shared = new SharedObjectPool<T, NullableS<T>, T>();
#endif
        GCCallback<T> _ = new(Shared);
    }
}
