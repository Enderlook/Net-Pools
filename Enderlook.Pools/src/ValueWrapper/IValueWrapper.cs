using System.Diagnostics.CodeAnalysis;

namespace Enderlook.Pools;

internal interface IValueWrapper<T>
{
    bool NotSynchronizedHasValue { get; }

    bool TryPopValue([NotNullWhen(true)] out T value);

    bool TrySetValue(ref T value);
}
