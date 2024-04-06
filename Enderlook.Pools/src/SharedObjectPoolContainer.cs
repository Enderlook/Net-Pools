using System;
using System.Diagnostics.CodeAnalysis;

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
        Shared = (ObjectPool<T>)Activator.CreateInstance((typeof(T).IsValueType ?
            typeof(SharedValueObjectPool<>)
            : typeof(SharedObjectPool<>))
            .MakeGenericType(typeof(T)))!;
        GCCallback<T> _ = new(Shared);
    }
}
