using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Enderlook.Pools;

#if NET7_0_OR_GREATER
/// <summary>
/// Provides a lease used to return objects to the pool.
/// </summary>
public readonly ref struct ObjectLease<T> : IDisposable
{
    // We store the pool here as it make easier for the JIT to devirtualize the `Return` call.
    internal readonly IReturnable<T> pool;
    private readonly ref ObjectOwner<T> owner;

    /// <summary>
    /// Retrieves the pooled value.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the instance was already disposed (so the value got returned to the pool).</exception>
    public T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (typeof(T).IsValueType)
            {
                Utils.OwnedLock(ref owner.State);
                T copy = owner.Value;
                Volatile.Write(ref owner.State, Utils.LEASE_USABLE);
                return copy;
            }
            else
            {
                T copy = owner.Value;
                if (copy is null)
                    Utils.ThrowObjectDisposedException_this();
                return copy;
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            if (typeof(T).IsValueType)
            {
                Utils.OwnedLock(ref owner.State);
                owner.Value = value;
                Volatile.Write(ref owner.State, Utils.LEASE_USABLE);
            }
            else
            {
                object copy = value;
                while (true)
                {
                    if (copy is null)
                        Utils.ThrowObjectDisposedException_this();
                    object t = Interlocked.CompareExchange(ref Unsafe.As<T, object?>(ref owner.Value), value, copy);
                    if (ReferenceEquals(t, copy))
                        break;
                    copy = t;
                }
            }
        }
    }

    /// <summary>
    /// Returns a mutable reference to the stored value.
    /// </summary>
    /// <returns>A reference to the stored value.</returns>
    /// <remarks>This is unsafe because the lease can be disposed while the reference still exists.<br/>
    /// The mutable reference is for better performance when mutating value types, but you should never replace the value with another one.</remarks>
    /// <exception cref="ObjectDisposedException">Thrown when the instance was already disposed (so the value got returned to the pool).</exception>
    public ref T UnsafeValueRef
    {
        get
        {
            if (typeof(T).IsValueType)
            {
                SpinWait spin = new();
                while (true)
                {
                    int v = Volatile.Read(ref owner.State);
                    if (v == Utils.LEASE_DISPOSED)
                        Utils.ThrowObjectDisposedException_this();
                    if (v == Utils.LEASE_USABLE)
                        break;
                    spin.SpinOnce();
                }
            }
            else if (owner.Value is null)
                Utils.ThrowObjectDisposedException_this();
            return ref owner.Value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ObjectLease(IReturnable<T> pool, ref ObjectOwner<T> owner)
    {
        this.pool = pool;
        this.owner = ref owner;
    }

    /// <summary>
    /// Returns the objects to the pool.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Dispose()
    {
        if (typeof(T).IsValueType)
        {
            SpinWait spin = new();
            while (true)
            {
                int v = Interlocked.CompareExchange(ref owner.State, Utils.LEASE_DISPOSED, Utils.LEASE_USABLE);
                if (v == Utils.LEASE_DISPOSED)
                    return;
                if (v == Utils.LEASE_USABLE)
                    break;
                spin.SpinOnce();
            }

            try
            {
                pool.Return(owner.Value, owner.State);
            }
            finally
            {
                owner.Value = default!;
            }
        }
        else
        {
            object? o = Interlocked.Exchange(ref Unsafe.As<T, object?>(ref owner.Value), null);
            if (o is not null)
                pool.Return(Unsafe.As<object, T>(ref o), owner.State);
        }
    }
}

internal interface IReturnable<T>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(T item, int state);
}
#endif