using System.Runtime.CompilerServices;
using System.Threading;
using System.Diagnostics.CodeAnalysis;

namespace Enderlook.Pools
{
    internal struct ValueObjectWrapper<T> where T : struct
    {
        private T value;
        private bool hasValue;
        private int @lock;

        public bool HasValue
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => hasValue;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryPopValue([NotNullWhen(true)] out T value)
        {
            while (Interlocked.Exchange(ref @lock, 1) == 1) ;

            if (hasValue)
            {
                value = this.value;
                this.value = default;
                hasValue = false;
                @lock = 0;
                return true;
            }

#if NET5_0_OR_GREATER
            Unsafe.SkipInit(out value);
#else
            value = default;
#endif
            @lock = 0;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TrySetValue(ref T value)
        {
            while (Interlocked.Exchange(ref @lock, 1) == 1) ;

            if (hasValue)
            {
                @lock = 0;
                return false;
            }

            this.value = value;
            hasValue = true;
            @lock = 0;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            while (Interlocked.Exchange(ref @lock, 1) == 1) ;
            hasValue = false;
            value = default;
            @lock = 0;
        }
    }
}
