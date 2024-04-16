using System.Runtime.CompilerServices;

namespace Enderlook.Pools;

internal sealed class SharedThreadLocalElement<T>
{
    public T? Value;
    public int MillisecondsTimeStamp;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T? ReplaceWith(T value)
    {
        T? previous = Value;
        Value = value;
        MillisecondsTimeStamp = 0;
        return previous;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Clear()
    {
        if (!typeof(T).IsValueType)
        {
            Value = default;
            MillisecondsTimeStamp = 0;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Trim(int currentMilliseconds, uint millisecondsThreshold)
    {
        if (!typeof(T).IsValueType)
        {
            // We treat 0 to mean it hasn't yet been seen in a Trim call.
            // In the very rare case where Trim records 0, it'll take an extra Trim call to remove the object.
            int lastSeen = MillisecondsTimeStamp;
            if (lastSeen == 0)
                MillisecondsTimeStamp = currentMilliseconds;
            else if ((currentMilliseconds - lastSeen) >= millisecondsThreshold)
            {
                // Time noticeably wrapped, or we've surpassed the threshold.
                // Clear out the array.
                Value = default;
            }
        }
    }
}