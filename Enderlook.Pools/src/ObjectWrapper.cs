using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Enderlook.Pools;

#if DEBUG
[DebuggerDisplay("{Value}")]
#endif
internal struct ObjectWrapper // Prevent runtime covariant checks on array access.
{
    public object? Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ObjectWrapper(object? value)
    {
        Value = value;
    }
}
