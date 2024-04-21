using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Enderlook.Pools;

internal static class SharedPoolHelpers
{
    /// <summary>
    /// Maximum length of `perCoreStacks` to use.
    /// </summary>
    public const int MaximumPerCoreStack = 64; // Selected to avoid needing to worry about processor groups.

    /// <summary>
    /// The maximum number of objects to store in each per-core stack.
    /// </summary>
    public const int MaxObjectsPerCore = 128;

    /// <summary>
    /// The initial capacity of `globalReserve`.
    /// </summary>
    public const int InitialGlobalReserveCapacity = 256;

    /// <summary>
    /// Number of locked stacks to employ.
    /// </summary>
    public static readonly int PerCoreStacksCount = Math.Min(Environment.ProcessorCount, MaximumPerCoreStack);

    public static int GetAllThreadLocalsCountReference(ref GCHandle[]? allThreadLocalElements, ref int allThreadLocalCount)
    {
        GCHandle[]? array = Unsafe.As<GCHandle[]>(Utils.NullExchange(ref allThreadLocalElements));
        int count = 0;
        ref GCHandle current = ref Utils.GetArrayDataReference(array);
        ref GCHandle end2 = ref Unsafe.Add(ref current, allThreadLocalCount);
        while (Unsafe.IsAddressLessThan(ref current, ref end2))
        {
            SharedThreadLocalElementReference? sharedThreadLocalElement = Unsafe.As<SharedThreadLocalElementReference?>(current.Target);
            if (sharedThreadLocalElement is not null)
            {
                object? value = sharedThreadLocalElement.Value;
                if (value is not null)
                    count++;
            }
            current = Unsafe.Add(ref current, 1);
        }
        allThreadLocalElements = array;
        return count;
    }

    public static int GetGlobalReserveCount(ref Array? array, ref int count)
    {
        Array? array_ = Utils.NullExchange(ref array);
        int count_ = count;
        array = array_;
        return count_;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetStartingIndex()
    {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        int currentProcessorId = Thread.GetCurrentProcessorId();
#else
        int currentProcessorId = Thread.CurrentThread.ManagedThreadId; // TODO: This is probably a bad idea.
#endif
        return (int)((uint)currentProcessorId % (uint)PerCoreStacksCount);
    }

    public static SharedThreadLocalElement GetOrCreateThreadLocal(ref SharedThreadLocalElement? threadLocalElement, SharedThreadLocalElement newThreadLocal, bool hasFinalizer, ref GCHandle[]? handles, ref int count)
    {
        SharedThreadLocalElement? slot = Interlocked.CompareExchange(ref threadLocalElement, newThreadLocal, null);
        if (slot is not null)
        {
            if (hasFinalizer)
            {
#pragma warning disable CA1816 // Dispose methods should call SuppressFinalize
                GC.SuppressFinalize(slot);
#pragma warning restore CA1816 // Dispose methods should call SuppressFinalize
            }
            return slot;
        }

        GCHandle[] allThreadLocalElements = Utils.NullExchange(ref handles);

        int count_ = count;
        if (unchecked((uint)count_ >= (uint)allThreadLocalElements.Length))
        {
            ref GCHandle current = ref Utils.GetArrayDataReference(allThreadLocalElements);
            ref GCHandle end = ref Unsafe.Add(ref current, allThreadLocalElements.Length);

            while (Unsafe.IsAddressLessThan(ref current, ref end))
            {
                GCHandle handle = current;
                Debug.Assert(handle.IsAllocated);
                object? target = handle.Target;
                if (target is null)
                {
                    handle.Target = newThreadLocal;
                    goto end;
                }
                current = ref Unsafe.Add(ref current, 1);
            }

            Array.Resize(ref allThreadLocalElements, allThreadLocalElements.Length * 2);
        }

        Debug.Assert(count_ < allThreadLocalElements.Length);
        Unsafe.Add(ref Utils.GetArrayDataReference(allThreadLocalElements), count_) = GCHandle.Alloc(newThreadLocal, GCHandleType.Weak);
        count = count_ + 1;

    end:
        handles = allThreadLocalElements;
        return newThreadLocal;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryPush<T>(this ref SharedPerCoreStack stack, T element)
    {
        Array array = stack.Array;
        Debug.Assert(array is T?[]);
        T?[] items = Unsafe.As<T?[]>(array);

        int count = Utils.MinusOneExchange(ref stack.Count);

        bool enqueued = false;
        if (unchecked((uint)count < (uint)items.Length))
        {
            if (count == 0)
            {
                // Reset the time stamp now that we're transitioning from empty to non-empty.
                // Trim will see this as 0 and initialize it to the current time when Trim is called.
                stack.MillisecondsTimeStamp = 0;
            }

            Debug.Assert(count < items.Length);
            Unsafe.Add(ref Utils.GetArrayDataReference(items), count++) = element;
            enqueued = true;
        }

        stack.Count = count;
        return enqueued;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryPop<T>(this ref SharedPerCoreStack stack, out T element)
    {
        Debug.Assert(stack.Array is T?[]);
        T?[] items = Unsafe.As<T?[]>(stack.Array);

        int count = Utils.MinusOneExchange(ref stack.Count);

        int newCount = count - 1;
        if (unchecked((uint)newCount < (uint)items.Length))
        {
            Debug.Assert(newCount < items.Length);
            ref T? slot = ref Unsafe.Add(ref Utils.GetArrayDataReference(items), newCount);
            Debug.Assert(slot is not null);
            element = slot;
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
#endif
                slot = default;
            stack.Count = newCount;
            return true;
        }

#if NET5_0_OR_GREATER
        Unsafe.SkipInit(out element);
#else
        element = default;
#endif

        stack.Count = count;
        return false;
    }

    public static bool FillFromGlobalReserve<T>(this ref SharedPerCoreStack stack, [NotNullWhen(true)] out T? element, ref T?[]? globalReserve, ref int globalReserveCount)
    {
        int count_ = Utils.MinusOneExchange(ref stack.Count);
        T?[] globalReserve_ = Utils.NullExchange(ref globalReserve);

        int globalCount = globalReserveCount;
        bool found;
        if (globalCount > 0)
        {
            Debug.Assert(globalCount - 1 < globalReserve_.Length);
            element = Unsafe.Add(ref Utils.GetArrayDataReference(globalReserve_), --globalCount);
            found = true;
            Debug.Assert(element is not null);

            Debug.Assert(stack.Array is T?[]);
            T?[] items = Unsafe.As<T?[]>(stack.Array);

            int length = Math.Min(MaxObjectsPerCore - count_, globalCount);
            int start = globalCount - length;
            Array.Copy(globalReserve_, start, items, count_, length);
            Array.Clear(globalReserve_, start, length);

            globalCount = start;
            count_ += length;

            globalReserveCount = globalCount;
        }
        else
        {
            found = false;
#if NET5_0_OR_GREATER
            Unsafe.SkipInit(out element);
#else
            element = default;
#endif
        }

        globalReserve = globalReserve_;
        stack.Count = count_;
        return found;
    }

    public static void MoveToGlobalReserve<T>(this ref SharedPerCoreStack stack, T obj, ref T?[]? globalReserve, ref int globalReserveCount)
    {
        int count_ = Utils.MinusOneExchange(ref stack.Count);
        T?[] globalReserve_ = Utils.NullExchange(ref globalReserve);

        Debug.Assert(stack.Array is T?[]);
        T?[] items = Unsafe.As<T?[]>(stack.Array);
        int amount = count_ + 1;
        int globalCount = globalReserveCount;
        int newGlobalCount = globalCount + amount;
        if (unchecked((uint)newGlobalCount >= (uint)globalReserve_.Length))
            Array.Resize(ref globalReserve_, globalReserve_.Length * 2);
        Array.Copy(items, 0, globalReserve_, globalCount, count_);
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
#endif
#if NET6_0_OR_GREATER
            Array.Clear(items);
#else
            Array.Clear(items, 0, items.Length);
#endif
        globalCount += count_;
        count_ = 0;
        Debug.Assert(globalCount < globalReserve_.Length);
        Unsafe.Add(ref Utils.GetArrayDataReference(globalReserve_), globalCount++) = obj;

        globalReserveCount = globalCount;
        globalReserve = globalReserve_;
        stack.Count = count_;
    }
}