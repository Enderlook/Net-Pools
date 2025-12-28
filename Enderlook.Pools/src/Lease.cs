using System;
using System.Runtime.CompilerServices;

namespace Enderlook.Pools;

/// <summary>
/// Represent a pooled value.<br/>
/// When <see cref="IDisposable.Dispose"/> is executed, the value is returned to the pool.
/// </summary>
/// <remarks>This type doesn't provide any double-dipose, default value or use after dispose safety.<br/>
/// This is to ensure the highest performance of the type.<br/><br/>
/// Users must ensure:
/// <list type="bullet">
/// <item>The instance is disposed exactly once.</item>
/// <item>The field <see cref="Value"/> is not used after disposing.</item>
/// <item>The instance is always obtained from a pool rather than using <see langword="default"/> (<c>default(<see cref="Lease{T}"/>)</c>) or calling its parameterless constructor.</item>
/// <item>Treat the instance under move-semantics. This particularly important if <typeparamref name="T"/> is a mutable value type, as mutations in <see cref="Value"/> would differ between copies.</item>
/// </list>
/// </remarks>
/// <typeparam name="T">Type of value to pool.</typeparam>
public struct Lease<T> : IDisposable
{
    private readonly IReturnable<T> pool;
    private readonly bool clearOnReturn;

    /// <summary>
    /// Value that is being rented.
    /// </summary>
    /// <remarks>This is a field so when <c>T</c> is a mutable value type it can be mutated.<br/>
    /// However, it's not intended for users to replace the value with another one never.</remarks>
    public T Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Lease(IReturnable<T> pool, bool clearOnReturn, T value)
    {
        this.pool = pool;
        this.clearOnReturn = clearOnReturn;
        Value = value;
    }

    /// <summary>
    /// Returns the value to the pool.
    /// </summary>
    /// <remarks>This method must be executed exactly once per instance.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Dispose() => pool.Return(Value, clearOnReturn);
}

internal interface IReturnable<T>
{
    public void Return(T value, bool clearOnReturn);
}