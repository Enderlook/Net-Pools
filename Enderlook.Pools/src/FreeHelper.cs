using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Enderlook.Pools;

internal static class FreeHelper
{
    public static bool ClearPool(ObjectWrapper[] items, int trimCount)
    {
        if (trimCount == 0)
            return false;

        int length = items.Length;
        if (length > trimCount)
        {
            ref ObjectWrapper current = ref Utils.GetArrayDataReference(items);
            ref ObjectWrapper end = ref Unsafe.Add(ref current, length);
            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                if (current.Value is not null)
                {
                    // Intentionally not using interlocked here.
                    current.Value = null;
                    if (--trimCount == 0)
                        return true;
                }
                current = ref Unsafe.Add(ref current, 1);
            }
        }
        else
        {
            // Intentionally not using interlocking in the array's elements.
#if NET6_0_OR_GREATER
            Array.Clear(items);
#else
            Array.Clear(items, 0, length);
#endif
        }
        return false;
    }

    public static bool ClearPool<T>(ValueObjectWrapper<T>[] items, int trimCount)
        where T : struct
    {
        if (trimCount == 0)
            return false;

        int length = items.Length;
        if (length > trimCount)
        {
            ref ValueObjectWrapper<T> current = ref Utils.GetArrayDataReference(items);
            ref ValueObjectWrapper<T> end = ref Unsafe.Add(ref current, length);
            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                if (current.NotSynchronizedHasValue
                    && current.TryPopValue(out _)
                    && --trimCount == 0)
                    return true;
                current = ref Unsafe.Add(ref current, 1);
            }
        }
        else
        {
            ref ValueObjectWrapper<T> current = ref Utils.GetArrayDataReference(items);
            ref ValueObjectWrapper<T> end = ref Unsafe.Add(ref current, length);
            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                if (current.NotSynchronizedHasValue)
                    current.TryPopValue(out _);
                current = ref Unsafe.Add(ref current, 1);
            }
        }
        return false;
    }

    public static bool ClearPool<T, U>(U item, T[] items, int trimCount)
        where U : IFreePool<T>
    {
        if (trimCount == 0)
            return false;

        int length = items.Length;
        ref T current = ref Utils.GetArrayDataReference(items);
        ref T end = ref Unsafe.Add(ref current, length);
        if (length > trimCount)
        {
            Debug.Assert(items.Length == length);
            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                if (item.TryFree(ref current) && --trimCount == 0)
                    return true;
                current = ref Unsafe.Add(ref current, 1);
            }
        }
        else
        {
            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                item.TryFree(ref current);
                current = ref Unsafe.Add(ref current, 1);
            }
        }
        return false;
    }

    public static void ClearReserve<T, U>(U item, T[] items, int oldCount, int newCount)
        where U : IFreeReserve<T>
    {
        Debug.Assert(oldCount >= newCount);
        Debug.Assert(oldCount <= items.Length);
        ref T current = ref Utils.GetArrayDataReference(items);
        ref T end = ref Unsafe.Add(ref current, oldCount);
        current = ref Unsafe.Add(ref current, newCount);
        while (Unsafe.IsAddressLessThan(ref current, ref end))
        {
            item.Free(ref current);
            current = ref Unsafe.Add(ref current, 1);
        }
    }

    public interface IFreePool<T>
    {
        bool TryFree(ref T value);
    }

    public interface IFreeReserve<T>
    {
        void Free(ref T value);
    }

    public struct CallDispose : IFreePool<ObjectWrapper>, IFreeReserve<ObjectWrapper>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Free(ref ObjectWrapper value)
        {
            object? element = value.Value;
            Debug.Assert(element is not null);
            Unsafe.As<IDisposable>(element).Dispose();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryFree(ref ObjectWrapper value)
        {
            object? element = value.Value;
            if (element is not null && ReferenceEquals(Interlocked.CompareExchange(ref value.Value, null, element), element))
            {
                Unsafe.As<IDisposable>(element).Dispose();
                return true;
            }
            return false;
        }
    }

    public struct CallDispose<T> : IFreePool<ValueObjectWrapper<T>>, IFreeReserve<T>
        where T : struct
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Free(ref T value) => ((IDisposable)value).Dispose();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryFree(ref ValueObjectWrapper<T> value)
        {
            if (value.NotSynchronizedHasValue && value.TryPopValue(out T element))
            {
                ((IDisposable)element).Dispose();
                return true;
            }
            return false;
        }
    }

    public struct TryCallDispose : IFreePool<ObjectWrapper>, IFreeReserve<ObjectWrapper>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Free(ref ObjectWrapper value)
        {
            Debug.Assert(value.Value is not null);
            if (value.Value is IDisposable disposable)
                disposable.Dispose();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryFree(ref ObjectWrapper value)
        {
            object? element = value.Value;
            if (element is not null && ReferenceEquals(Interlocked.CompareExchange(ref value.Value, null, element), element))
            {
                if (element is IDisposable disposable)
                    disposable.Dispose();
                return true;
            }
            return false;
        }
    }

    public struct CustomObjectFree<T> : IFreePool<ObjectWrapper>, IFreeReserve<ObjectWrapper>
        where T : class
    {
        private readonly Action<T> action;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CustomObjectFree(Action<T> action)
        {
            Debug.Assert(action is not null);
            this.action = action;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Free(ref ObjectWrapper value)
        {
            object? element = value.Value;
            Debug.Assert(element is not null);
            action(Unsafe.As<T>(element));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryFree(ref ObjectWrapper value)
        {
            object? element = value.Value;
            if (element is not null && ReferenceEquals(Interlocked.CompareExchange(ref value.Value, null, element), element))
            {
                action(Unsafe.As<T>(element));
                return true;
            }
            return false;
        }
    }

    public struct CustomValueObjectFree<T> : IFreePool<ValueObjectWrapper<T>>, IFreeReserve<T>
        where T : struct
    {
        private readonly Action<T> action;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CustomValueObjectFree(Action<T> action)
        {
            Debug.Assert(action is not null);
            this.action = action;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Free(ref T value) => action(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryFree(ref ValueObjectWrapper<T> value)
        {
            if (value.NotSynchronizedHasValue && value.TryPopValue(out T element))
            {
                action(element);
                return true;
            }
            return false;
        }
    }
}