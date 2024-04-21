using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Enderlook.Pools;

internal static class Disposing<T>
{
    public const byte IMPLEMENT_IDISPOSABLE = 1;
    public const byte MAY_IMPLEMENT_IDISPOSABLE = 2;
    public const byte HAS_CUSTOM_DISPOSING = 3;
    public const byte CAN_NOT_IMPLEMENT_IDISPOSABLE = 4;
    public const byte NULL_CUSTOM_DISPOSING = 5;

    public const byte IMPLEMENT_IDISPOSABLE_IS_WRAPPED = 6;

    public static readonly byte DisposableMode = typeof(IDisposable).IsAssignableFrom(typeof(T))
        ? IMPLEMENT_IDISPOSABLE
        : typeof(T).IsSealed
            ? CAN_NOT_IMPLEMENT_IDISPOSABLE
            : MAY_IMPLEMENT_IDISPOSABLE;

    public static readonly byte DisposableMode2 = typeof(IDisposable).IsAssignableFrom(typeof(T))
        ? (typeof(T).IsValueType ? IMPLEMENT_IDISPOSABLE_IS_WRAPPED : IMPLEMENT_IDISPOSABLE)
        : typeof(T).IsSealed
            ? CAN_NOT_IMPLEMENT_IDISPOSABLE
            : MAY_IMPLEMENT_IDISPOSABLE;

    public static readonly Action<T>? DisposeAction = DisposableMode switch
    {
        IMPLEMENT_IDISPOSABLE => e =>
        {
            Debug.Assert(e is not null);
            Unsafe.As<IDisposable>(e).Dispose();
        },
        MAY_IMPLEMENT_IDISPOSABLE => e =>
        {
            if (e is IDisposable disposable)
                disposable.Dispose();
        },
        _ => _ => { },
    };
}