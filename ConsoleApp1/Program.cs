using Enderlook.Pools;

using System.Diagnostics;
using System.Runtime.CompilerServices;

internal class Z
{
    public static void Main()
    {
        /*
        Stack<UVDA> q = new();
        for (int j = 0; j < 2; j++)
        {
            for (int i = 0; i < 10000; i++)
                q.Push(SharedRent2());
            while (q.TryPop(out var v))
                SharedReturn2(v);
            Thread.Sleep(100);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static UVDA SharedRent2() => ObjectPool<UVDA>.Shared.Rent();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void SharedReturn2(UVDA t) => ObjectPool<UVDA>.Shared.Return(t);

        SharedTest<UV>();
        SharedTest<UVDN>();
        SharedTest<UVDA>();
        SharedTest<MV>();
        SharedTest<MVD>();
        SharedTest<RMD>();
        SharedTest<RD>();
        SharedTest<RNV>();

        Console.WriteLine("Shared End");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();*/

        InstanceTest<UV>();
        InstanceTest<UVDN>();
        InstanceTest<UVDA>();
        InstanceTest<MV>();
        InstanceTest<MVD>();
        InstanceTest<RMD>();
        InstanceTest<RD>();
        InstanceTest<RNV>();

        Console.WriteLine("Instance End");
        /*
        FastObjectPool<object> pool = new();
        For(0, 100, _ =>
        {
            Stack<object> q = new();
            for (int i = 0; i < 1000; i++)
                q.Push(pool.Rent());
            while (q.TryPop(out object? v))
                pool.Return(v);
        });
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Console.WriteLine("Fast End");*/

        return;

        /* For(0, 100, _ =>
         {
             var r = ExactLengthArrayPool<int>.SharedOf(16);
             Stack<int[]> q = new();
             for (int i = 0; i < 1000; i++)
                 q.Push(r.Rent());
             while (q.TryPop(out int[]? v))
                 r.Return(v);
         });
         ExactLengthArrayPool<int>.SharedOf(16).Trim(true);
         GC.Collect();
         GC.WaitForPendingFinalizers();

         For(0, 16, l =>
         {
             For(0, 100, _ =>
             {
                 Stack<int[]> q = new();
                 for (int i = 0; i < 1000; i++)
                     q.Push(SharedRent3(l));
                 while (q.TryPop(out int[]? v))
                     SharedReturn3(v);
             });
         });
         Trim();
         GC.Collect();
         GC.WaitForPendingFinalizers();

         [MethodImpl(MethodImplOptions.NoInlining)]
         static int[] SharedRent3(int length) => ExactLengthArrayPool<int>.Shared.Rent(length);

         [MethodImpl(MethodImplOptions.NoInlining)]
         static void SharedReturn3(int[] array) => ExactLengthArrayPool<int>.Shared.Return(array);

         [MethodImpl(MethodImplOptions.NoInlining)]
         static void Trim() => ExactLengthArrayPool<int>.Shared.Trim(true);*/

        Console.WriteLine("END");
    }

    private static void SharedTest<T>()
    {
        Console.WriteLine(typeof(T));
        For(0, 100, _ =>
        {
            Stack<T> q = new();
            for (int i = 0; i < 10000; i++)
                q.Push(SharedRent<T>());
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
    {
        Console.WriteLine(typeof(T));
        SafeObjectPool<T> pool = new();
        For(0, 100, _ =>
        {
            Stack<T> q = new();
            for (int i = 0; i < 1000; i++)
                q.Push(Rent(pool));
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

    private static void For(int from, int to, Action<int> action)
    {
        /*for (int i = from; i < to; i++)
            action(i);

        return;*/
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

    private struct UV { }

    private struct UVDA : IDisposable
    {
        private int o;

        public UVDA()
        {
            o = 1;
        }

        public void Dispose()
        {
            Debug.Assert(o == 1);
            o = 2;
        }
    }

    private struct UVDN : IDisposable
    {
        private int q;
        private int q2;
        private int q3;
        private int q4;
        private int q5;

        public void Dispose() { }
    }

    private struct MV
    {
        private object o;
    }

    private struct MVD : IDisposable
    {
        private Q o;

        public MVD()
        {
            o = new();
        }

        public void Dispose() => o.Dispose();

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

    public class RMD { }

    [DebuggerDisplay("{w} {disposed}")]
    public class RD : IDisposable
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

    public sealed class RNV { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SharedTrim<T>() => ObjectPool<T>.Shared.Trim(true);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T SharedRent<T>() => ObjectPool<T>.Shared.Rent();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SharedReturn<T>(T t) => ObjectPool<T>.Shared.Return(t);
}