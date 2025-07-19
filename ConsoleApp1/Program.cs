﻿using Enderlook.Pools;

using System.Diagnostics;
using System.Runtime.CompilerServices;

internal class Z
{
    public static void Main()
    {
        Console.WriteLine("Shared Start");
        SharedTest<UVZ>();
        SharedTest<UVDN>();
        SharedTest<UVDA>();
        SharedTest<MV>();
        SharedTest<MVD>();
        SharedTest<RMD>();
        SharedTest<RD>();
        SharedTest<RNV>();
        Console.WriteLine("Shared End");

        Console.WriteLine("Instance Start");
        InstanceTest<UVZ>();
        InstanceTest<UVDN>();
        InstanceTest<UVDA>();
        InstanceTest<MV>();
        InstanceTest<MVD>();
        InstanceTest<RMD>();
        InstanceTest<RD>();
        InstanceTest<RNV>();
        Console.WriteLine("Instance End");

        Console.WriteLine("Fast Start");
        FastObjectPool<object> pool = new();
        For(0, 100, _ =>
        {
            Stack<object> q = new();
            for (int i = 0; i < 1000; i++)
                q.Push(Rent(pool));
            while (q.TryPop(out object? v))
                Return(pool, v);
        });
        Trim(pool);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Console.WriteLine("Fast End");

        Console.WriteLine("Shared Array Start");

        For(0, 100, _ =>
        {
            var r = ExactLengthArrayPool<int>.SharedOfLength(16);
            Stack<int[]> q = new();
            for (int i = 0; i < 1000; i++)
                q.Push(r.Rent());
            while (q.TryPop(out int[]? v))
                r.Return(v);
        });
        ExactLengthArrayPool<int>.SharedOfLength(16).Trim(true);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        For(0, 100, _ =>
        {
            var r = ExactLengthArrayPool<byte>.Shared.OfLength(16);
            Stack<byte[]> q = new();
            for (int i = 0; i < 1000; i++)
                q.Push(r.Rent());
            while (q.TryPop(out byte[]? v))
                r.Return(v);
            GC.Collect();
        });
        ExactLengthArrayPool<int>.SharedOfLength(16).Trim(true);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        For(0, 16, l =>
        {
            For(0, 100, _ =>
            {
                Stack<int[]> q = new();
                for (int i = 0; i < 1000; i++)
                    q.Push(SharedRentArray<int>(l));
                while (q.TryPop(out int[]? v))
                    SharedReturnArray(v);
                GC.Collect();
            });
        });
        SharedTrimArray<int>();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        Console.WriteLine("Shared Array End");

        Console.WriteLine("Instace Array Start");

        SafeExactLengthArrayObjectPool<int> pool2 = new(16);
        For(0, 100, _ =>
        {
            Stack<int[]> q = new();
            for (int i = 0; i < 1000; i++)
                q.Push(Rent(pool2));
            while (q.TryPop(out int[]? v))
                Return(pool2, v);
        });
        Trim(pool2);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        SafeExactLengthArrayPool<int> pool3 = new();
        For(0, 100, _ =>
        {
            var r = pool3.OfLength(16);
            Stack<int[]> q = new();
            for (int i = 0; i < 1000; i++)
                q.Push(Rent(r));
            while (q.TryPop(out int[]? v))
                Return(r, v);
            GC.Collect();
        });
        Trim(pool3.OfLength(16));
        GC.Collect();
        GC.WaitForPendingFinalizers();

        For(0, 16, l =>
        {
            For(0, 100, _ =>
            {
                Stack<int[]> q = new();
                for (int i = 0; i < 1000; i++)
                    q.Push(RentArray(pool3, l));
                while (q.TryPop(out int[]? v))
                    ReturnArray(pool3, v);
                GC.Collect();
            });
        });
        TrimArray(pool3);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        Console.WriteLine("Instace Array End");

        Console.WriteLine("End");



        /*for (int i = 0; i < 10; i++)
        {
            for (int j = 0; j < 1000; j++)
            {
                var a = GetA(15);
                var b = GetB(15);
            }
            Thread.Sleep(10);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static ObjectPool<int[]> GetA(int i) => ExactLengthArrayPool<int>.SharedOfLength(i);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static ObjectPool<int[]> GetB(int i) => ExactLengthArrayPool<int>.Shared.OfLength(i);*/
    }

    private static void SharedTest<T>()
        where T : I
    {
        Console.WriteLine(typeof(T));
        For(0, 100, _ =>
        {
            Stack<T> q = new();
            for (int i = 0; i < 10000; i++)
            {
                T? t = SharedRent<T>();
                t.Assert();
                q.Push(t);
            }

            while (q.TryPop(out T? v))
                SharedReturn(v);
            GC.Collect();
        });
        GC.WaitForPendingFinalizers();
        SharedTrim<T>();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private static void InstanceTest<T>()
        where T : I
    {
        Console.WriteLine(typeof(T));
        SafeObjectPool<T> pool = new();
        Parallel.For(0, 100, _ =>
        {
            Stack<T> q = new();
            for (int i = 0; i < 1000; i++)
            {
                T t = Rent(pool);
                t.Assert();
                q.Push(t);
            }

            while (q.TryPop(out T? v))
                Return(pool, v);
        });
        Trim(pool);
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T Rent<T>(SafeObjectPool<T> pool) => pool.Rent();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Return<T>(SafeObjectPool<T> pool, T value) => pool.Return(value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Trim<T>(SafeObjectPool<T> pool) => pool.Trim(true);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T Rent<T>(FastObjectPool<T> pool) where T : class => pool.Rent();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Return<T>(FastObjectPool<T> pool, T value) where T : class => pool.Return(value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Trim<T>(FastObjectPool<T> pool) where T : class => pool.Trim(true);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T[] Rent<T>(SafeExactLengthArrayObjectPool<T> pool) => pool.Rent();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Return<T>(SafeExactLengthArrayObjectPool<T> pool, T[] value) => pool.Return(value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Trim<T>(SafeExactLengthArrayObjectPool<T> pool) => pool.Trim(true);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SharedTrim<T>() => ObjectPool<T>.Shared.Trim(true);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T SharedRent<T>() => ObjectPool<T>.Shared.Rent();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SharedReturn<T>(T t) => ObjectPool<T>.Shared.Return(t);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SharedTrimArray<T>() => ExactLengthArrayPool<T>.Shared.Trim(true);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T[] SharedRentArray<T>(int length) => ExactLengthArrayPool<T>.Shared.Rent(length);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SharedReturnArray<T>(T[] t) => ExactLengthArrayPool<T>.Shared.Return(t);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void TrimArray<T>(ExactLengthArrayPool<T> pool) => pool.Trim(true);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T[] RentArray<T>(ExactLengthArrayPool<T> pool, int length) => pool.Rent(length);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ReturnArray<T>(ExactLengthArrayPool<T> pool, T[] t) => pool.Return(t);

    private static void For(int from, int to, Action<int> action)
    {
        Thread[] threads = new Thread[to - from];
        for (int i = from, j = 0; i < to; i++, j++)
        {
            Thread thread = new(() => action(i));
            threads[j] = thread;
            thread.Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }
    }

    private interface I
    {
        void Assert() { }
    }

    private struct UVZ : I { }

    private struct UVDA : IDisposable, I
    {
        private byte a;
        private byte c;

        public UVDA()
        {
            a = 1;
            c = (byte)Random.Shared.Next();
        }

        public void Dispose()
        {
            Debug.Assert(a == 1);
            a = 2;
        }

        public void Assert()
        {
            Debug.Assert(a == 1);
        }
    }

    private struct UVDN : IDisposable, I
    {
        private int q;
        private int q2;
        private int q3;
        private int q4;
        private int q5;

        public UVDN()
        {
            q = 1;
            q2 = Random.Shared.Next();
        }

        public void Dispose()
        {
            Debug.Assert(q == 1);
            q = 2;
        }

        public void Assert()
        {
            Debug.Assert(q == 1);
        }
    }

    private struct MV : I
    {
        private object o;

        public MV()
        {
            o = new();
        }

        public void Assert()
        {
            Debug.Assert(o is not null);
        }
    }

    private struct MVD : IDisposable, I
    {
        private Q o = new();

        public MVD()
        {
        }

        public void Dispose() => o.Dispose();

        public void Assert()
        {
            Debug.Assert(o is not null);
        }

        class Q()
        {
            private bool disposed;

            ~Q()
            {
                //Console.WriteLine(disposed);
                Debug.Assert(!disposed);
            }

            public void Dispose()
            {
                GC.SuppressFinalize(this);
                Debug.Assert(!disposed);
                disposed = true;
            }
        }
    }

    public class RMD : I { }

    [DebuggerDisplay("{w} {disposed}")]
    public class RD : IDisposable, I
    {
        private static int q;

        private bool disposed;
        private int w = Interlocked.Increment(ref q);

        //private StackTrace t;

        ~RD()
        {
            //Console.WriteLine(disposed);
            Debug.Assert(!disposed);
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Debug.Assert(!disposed);
            disposed = true;
            //t = new StackTrace();
        }

        public override bool Equals(object? obj) => obj is RD r ? r.w == w : false;
    }

    public sealed class RNV : I { }
}