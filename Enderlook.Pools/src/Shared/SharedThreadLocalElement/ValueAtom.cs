using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Enderlook.Pools;

[StructLayout(LayoutKind.Sequential, Size = 8)]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
internal struct ValueAtom<T>(T value)
{
    public bool Has = true;
    public T Value = value;
}