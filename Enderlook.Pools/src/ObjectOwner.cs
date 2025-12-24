using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Enderlook.Pools;

#if NET7_0_OR_GREATER
/// <summary>
/// Represent the location in which the owned object is stored.<br/>
/// This value should not be mutated by user code nor moved.
/// </summary>
public struct ObjectOwner<T>
{
    internal int State;
    internal T Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ObjectOwner(T value)
    {
        State = Utils.LEASE_USABLE;
        Value = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ObjectOwner(T value, int state)
    {
        Debug.Assert(!typeof(T).IsValueType);
        State = state;
        Value = value;
    }
}
#endif