using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

    public override bool Equals([NotNullWhen(true)] object? obj)
        => obj is ObjectWrapper w ? w.Value == Value : false;
}
