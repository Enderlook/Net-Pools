using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Enderlook.Pools;

internal static class ObjectPoolHelper
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

    public static bool ClearPool<T, U>(U[] items, int trimCount)
        where U : IValueWrapper<T>
    {
        if (trimCount == 0)
            return false;

        int length = items.Length;
        if (length > trimCount)
        {
            ref U current = ref Utils.GetArrayDataReference(items);
            ref U end = ref Unsafe.Add(ref current, length);
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
            ref U current = ref Utils.GetArrayDataReference(items);
            ref U end = ref Unsafe.Add(ref current, length);
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

    public static void ClearReserve<T, U>(U item, T?[] items, int oldCount, int newCount)
        where U : IFreeReserve<T>
    {
        Debug.Assert(oldCount >= newCount);
        Debug.Assert(oldCount <= items.Length);
        ref T? current = ref Utils.GetArrayDataReference(items);
        ref T? end = ref Unsafe.Add(ref current, oldCount);
        current = ref Unsafe.Add(ref current, newCount);
        while (Unsafe.IsAddressLessThan(ref current, ref end))
        {
            Debug.Assert(current is not null);
            item.Free(ref current);
            current = ref Unsafe.Add(ref current, 1)!;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T FillFromReserveValue<T, U>(this SafeObjectPool<T> self)
        where U : IValueWrapper<T>
    {
        Debug.Assert(self.array is U[]);
        U[] items = Unsafe.As<U[]>(self.array);
        Array reserve_ = Utils.NullExchange(ref self.reserve);
        Debug.Assert(reserve_ is T[]);
        T[] reserve = Unsafe.As<T[]>(reserve_);

        int reserveCount = self.reserveCount;
        if (reserveCount > 0)
        {
            int oldReserveCount = reserveCount;

            ref T startReserve = ref Utils.GetArrayDataReference(reserve);
            ref T currentReserve = ref Unsafe.Add(ref startReserve, reserveCount - 1);

#if DEBUG
            int i = 1;
#endif
            T value = currentReserve;
            currentReserve = ref Unsafe.Subtract(ref currentReserve, 1);
            Debug.Assert(--reserveCount >= 0);

            ref U currentItem = ref Utils.GetArrayDataReference(items);
            ref U endItem = ref Unsafe.Add(ref currentItem, Math.Min(oldReserveCount - 1, items.Length / 2));
            while (Unsafe.IsAddressLessThan(ref currentItem, ref endItem))
            {
                // Note that intitial read and write are optimistically not synchronized. This is intentional.
                // In a worst case we may miss some recently returned objects.
                // But we never accidentally free.
                if (!currentItem.NotSynchronizedHasValue)
                {
                    if (currentItem.TrySetValue(ref currentReserve))
                    {
#if DEBUG
                        i++;
#endif
                        currentReserve = ref Unsafe.Subtract(ref currentReserve, 1);
                        Debug.Assert(--reserveCount >= 0);
                    }
                }
                currentItem = ref Unsafe.Add(ref currentItem, 1);
            }

            int newReserveCount = (int)((long)Unsafe.ByteOffset(ref startReserve, ref currentReserve) / Unsafe.SizeOf<T>()) + 1;
            Debug.Assert(newReserveCount == reserveCount);
#if DEBUG
            Debug.Assert(i == oldReserveCount - newReserveCount);
#endif
            Array.Clear(reserve, newReserveCount, oldReserveCount - newReserveCount);
            self.reserveCount = newReserveCount;
            self.reserve = reserve;

            return value;
        }
        else
        {
            self.reserve = reserve;
            return self.factory();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T FillFromReserveReference<T>(this SafeObjectPool<T> self)
    {
        Debug.Assert(self.array is ObjectWrapper[]);
        ObjectWrapper[] items_ = Unsafe.As<ObjectWrapper[]>(self.array);
        Array reserve_ = Utils.NullExchange(ref self.reserve);
        Debug.Assert(reserve_ is ObjectWrapper[]);
        ObjectWrapper[] reserve = Unsafe.As<ObjectWrapper[]>(reserve_);

        int oldCount = self.reserveCount;
        if (oldCount > 0)
        {
            object element = FillFromReserve_(items_, reserve, ref oldCount);
            self.reserveCount = oldCount;
            self.reserve = reserve;
            Debug.Assert(element is T);
            return Unsafe.As<object, T>(ref element);
        }

        self.reserve = reserve;
        return self.factory();
    }

    private static object FillFromReserve_(ObjectWrapper[] items, ObjectWrapper[] reserve, ref int reserveCount)
    {
        int reserveCount_ = reserveCount;
        int oldReserveCount = reserveCount_;

        ref ObjectWrapper startReserve = ref Utils.GetArrayDataReference(reserve);
        ref ObjectWrapper currentReserve = ref Unsafe.Add(ref startReserve, reserveCount_ - 1);

#if DEBUG
        int i = 1;
#endif

        object value = currentReserve.Value!;
        Debug.Assert(value is not null);
        currentReserve = ref Unsafe.Subtract(ref currentReserve, 1);
        Debug.Assert(--reserveCount_ >= 0);

        ref ObjectWrapper currentItem = ref Utils.GetArrayDataReference(items);
        ref ObjectWrapper endItem = ref Unsafe.Add(ref currentItem, Math.Min(oldReserveCount - 1, items.Length / 2));
        while (Unsafe.IsAddressLessThan(ref currentItem, ref endItem))
        {
            // Note that intitial read and write are optimistically not synchronized. This is intentional.
            // In a worst case we may miss some recently returned objects.
            // But we never accidentally free, this is why we use CompareExchange to check for null.
            if (currentItem.Value is null)
            {
                if (Interlocked.CompareExchange(ref currentItem.Value, currentReserve.Value, null) is null)
                {
#if DEBUG
                    i++;
#endif
                    currentReserve = ref Unsafe.Subtract(ref currentReserve, 1);
                    Debug.Assert(--reserveCount_ >= 0);
                }
            }
            currentItem = ref Unsafe.Add(ref currentItem, 1);
        }

        int newReserveCount = (int)((long)Unsafe.ByteOffset(ref startReserve, ref currentReserve) / Unsafe.SizeOf<ObjectWrapper>()) + 1;
        Debug.Assert(newReserveCount == reserveCount_);
#if DEBUG
        Debug.Assert(i == oldReserveCount - newReserveCount);
#endif
        Array.Clear(reserve, newReserveCount, oldReserveCount - newReserveCount);
        reserveCount = newReserveCount;

        return value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SendToReserveValue<T, U>(this SafeObjectPool<T> self, T obj)
        where U : IValueWrapper<T>
    {
        Debug.Assert(self.array is U[]);
        U[] items = Unsafe.As<U[]>(self.array);
        Array reserve_ = Utils.NullExchange(ref self.reserve);
        Debug.Assert(reserve_ is T[]);
        T[] reserve = Unsafe.As<T[]>(reserve_);

        int reserveCount = self.reserveCount;
        int newCount = reserveCount + 1 + (items.Length / 2);
        if (newCount > reserve.Length)
        {
            if (self.IsReserveDynamic)
                Array.Resize(ref reserve, Math.Max(newCount, Math.Max(reserve.Length * 2, 1)));
            else if (reserveCount + 1 == reserve.Length)
            {
                switch (self.disposeMode)
                {
                    case Disposing<T>.IMPLEMENT_IDISPOSABLE:
                        Debug.Assert(obj is not null);
                        ((IDisposable)obj).Dispose();
                        break;
                    case Disposing<T>.HAS_CUSTOM_DISPOSING:
                        Action<T>? freeCallback_ = self.freeCallback;
                        Debug.Assert(freeCallback_ is not null);
                        freeCallback_(obj);
                        break;
                }
                self.reserve = reserve;
                return;
            }
        }

        ref T startReserve = ref Utils.GetArrayDataReference(reserve);
        ref T currentReserve = ref Unsafe.Add(ref startReserve, reserveCount);

        Debug.Assert(reserveCount++ <= reserve.Length);
        currentReserve = obj;
        currentReserve = ref Unsafe.Add(ref currentReserve, 1);

        ref U currentItem = ref Utils.GetArrayDataReference(items);
        ref U endItem = ref Unsafe.Add(ref currentItem, Math.Min(items.Length / 2, reserve.Length - reserveCount));
        while (Unsafe.IsAddressLessThan(ref currentItem, ref endItem))
        {
            // We don't use an optimistically not synchronized initial read in this part.
            // This is because we expect the majority of the array to be filled.
            // So it's not worth doing an initial read to check that.
            ref U item = ref currentItem;
            currentItem = ref Unsafe.Add(ref currentItem, 1);
            if (item.TryPopValue(out T element))
            {
                Debug.Assert(reserveCount++ <= reserve.Length);
                currentReserve = element;
                currentReserve = ref Unsafe.Add(ref currentReserve, 1);
            }
        }

        int newReserveCount = (int)((long)Unsafe.ByteOffset(ref startReserve, ref currentReserve) / Unsafe.SizeOf<T>());
        Debug.Assert(newReserveCount == reserveCount);
        self.reserveCount = newReserveCount;
        self.reserve = reserve;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SendToReserveReference<T>(this SafeObjectPool<T> self, T obj)
    {
        if (obj is null) return;

        Debug.Assert(self.array is ObjectWrapper[]);
        ObjectWrapper[] items = Unsafe.As<ObjectWrapper[]>(self.array);
        Array reserve_ = Utils.NullExchange(ref self.reserve);
        Debug.Assert(reserve_ is ObjectWrapper[]);
        ObjectWrapper[] reserve = Unsafe.As<ObjectWrapper[]>(reserve_);

        int reserveCount = self.reserveCount;
        int newCount = reserveCount + 1 + (items.Length / 2);
        if (newCount > reserve.Length)
        {
            if (self.IsReserveDynamic)
                Array.Resize(ref reserve, Math.Max(newCount, Math.Max(reserve.Length * 2, 1)));
            else if (reserveCount + 1 == reserve.Length)
            {
                switch (self.disposeMode)
                {
                    case Disposing<T>.IMPLEMENT_IDISPOSABLE:
                        Unsafe.As<IDisposable>(obj).Dispose();
                        break;
                    case Disposing<T>.MAY_IMPLEMENT_IDISPOSABLE when obj is IDisposable disposable:
                        disposable.Dispose();
                        break;
                    case Disposing<T>.HAS_CUSTOM_DISPOSING:
                        Action<T>? freeCallback_ = self.freeCallback;
                        Debug.Assert(freeCallback_ is not null);
                        freeCallback_(obj);
                        break;
                }
                self.reserve = reserve;
                return;
            }
        }

        int newReserveCount = SendToReserve_(obj, items, reserve, ref reserveCount);

        self.reserveCount = newReserveCount;
        self.reserve = reserve;
    }

    public static int SendToReserve_(object obj, ObjectWrapper[] items, ObjectWrapper[] reserve, ref int reserveCount)
    {
        ref ObjectWrapper startReserve = ref Utils.GetArrayDataReference(reserve);
        ref ObjectWrapper currentReserve = ref Unsafe.Add(ref startReserve, reserveCount);

        Debug.Assert(reserveCount++ <= reserve.Length);
        Debug.Assert(obj is not null);
        currentReserve.Value = obj;
        currentReserve = ref Unsafe.Add(ref currentReserve, 1);

        ref ObjectWrapper currentItem = ref Utils.GetArrayDataReference(items);
        ref ObjectWrapper endItem = ref Unsafe.Add(ref currentItem, Math.Min(items.Length / 2, reserve.Length - reserveCount));
        while (Unsafe.IsAddressLessThan(ref currentItem, ref endItem))
        {
            // We don't use an optimistically not synchronized initial read in this part.
            // This is because we expect the majority of the array to be filled.
            // So it's not worth doing an initial read to check that.
            ref ObjectWrapper item = ref currentItem;
            currentItem = ref Unsafe.Add(ref currentItem, 1);
            object? element = Interlocked.Exchange(ref currentItem.Value, null);
            if (element is not null)
            {
                Debug.Assert(reserveCount++ <= reserve.Length);
                currentReserve.Value = element;
                currentReserve = ref Unsafe.Add(ref currentReserve, 1);
            }
        }

        int newReserveCount = (int)((long)Unsafe.ByteOffset(ref startReserve, ref currentReserve) / Unsafe.SizeOf<ObjectWrapper>());
        Debug.Assert(newReserveCount == reserveCount);
        return newReserveCount;
    }

    public interface IFreePool<T>
    {
        bool TryFree(ref T value);
    }

    public interface IFreeReserve<T>
    {
        void Free(ref T value);
    }

    public struct CallDisposeReference : IFreePool<ObjectWrapper>, IFreeReserve<ObjectWrapper>
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

    public struct CallDisposePoolValue<T> : IFreePool<ValueMutex<T>>, IFreeReserve<T>
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        , IFreePool<ValueAtom<T>>
#endif
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryFree(ref ValueMutex<T> value)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            Debug.Assert(!(Unsafe.SizeOf<ValueAtom<T>>() == sizeof(long) && !RuntimeHelpers.IsReferenceOrContainsReferences<T>()));
#endif
            if (value.NotSynchronizedHasValue && value.TryPopValue(out T element))
            {
                ((IDisposable)element).Dispose();
                return true;
            }
            return false;
        }

#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryFree(ref ValueAtom<T> value)
        {
            Debug.Assert(Unsafe.SizeOf<ValueAtom<T>>() == sizeof(long) && !RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            if (value.NotSynchronizedHasValue && value.TryPopValue(out T element))
            {
                ((IDisposable)element).Dispose();
                return true;
            }
            return false;
        }
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Free(ref T value)
        {
            Debug.Assert(value is not null);
            ((IDisposable)value).Dispose();
        }
    }

    public struct TryCallDisposeReference : IFreePool<ObjectWrapper>, IFreeReserve<ObjectWrapper>
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

    public struct CustomFreeReference<T> : IFreePool<ObjectWrapper>, IFreeReserve<ObjectWrapper>
    {
        private readonly Action<T> action;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CustomFreeReference(Action<T> action)
        {
            Debug.Assert(action is not null);
            this.action = action;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Free(ref ObjectWrapper value)
        {
            object? element = value.Value;
            Debug.Assert(element is not null);
            action(Unsafe.As<object, T>(ref element));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryFree(ref ObjectWrapper value)
        {
            object? element = value.Value;
            if (element is not null && ReferenceEquals(Interlocked.CompareExchange(ref value.Value, null, element), element))
            {
                action(Unsafe.As<object, T>(ref element));
                return true;
            }
            return false;
        }
    }

    public struct CustomFreeValue<T> : IFreePool<ValueMutex<T>>, IFreeReserve<T>
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        , IFreePool<ValueAtom<T>>
#endif
    {
        private readonly Action<T> action;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CustomFreeValue(Action<T> action)
        {
            Debug.Assert(action is not null);
            this.action = action;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Free(ref T value) => action(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryFree(ref ValueMutex<T> value)
        {
            if (value.NotSynchronizedHasValue && value.TryPopValue(out T element))
            {
                action(element);
                return true;
            }
            return false;
        }

#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryFree(ref ValueAtom<T> value)
        {
            if (value.NotSynchronizedHasValue && value.TryPopValue(out T element))
            {
                action(element);
                return true;
            }
            return false;
        }
#endif
    }
}