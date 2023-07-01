using System.Diagnostics;

namespace Enderlook.Pools;

#if DEBUG
[DebuggerDisplay("{Value}")]
#endif
internal struct ObjectWrapper<T> // Prevent runtime covariant checks on array access.
{
    public T Value;
}
