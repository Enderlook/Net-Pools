using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Enderlook.Pools
{
    internal static class ObjectPoolHelper<T>
    {
        public static readonly Func<T> Factory;
        private static readonly bool useDefault;

        static ObjectPoolHelper()
        {
            // TODO: In .NET 7 Activator.CreateFactory<T>() may be added https://github.com/dotnet/runtime/issues/36194.

            ConstructorInfo? constructor = typeof(T).GetConstructor(Type.EmptyTypes);
            if (constructor is null)
            {
                if (typeof(T).IsValueType)
                {
                    useDefault = true;
                    Factory = () => default!;
                }
                else
                    Factory = () => throw new MissingMethodException($"No parameterless constructor defined for type '{typeof(T)}'.");
                return;
            }

            switch (Utilities.DynamicCompilationMode)
            {
                case Utilities.SystemLinqExpressions:
                    Factory = Expression.Lambda<Func<T>>(Expression.New(typeof(T)), Array.Empty<ParameterExpression>()).Compile();
                    break;
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
                case Utilities.SystemReflectionEmitDynamicMethod:
                    DynamicMethod dynamicMethod = new("Instantiate", typeof(T), Type.EmptyTypes);
                    ILGenerator generator = dynamicMethod.GetILGenerator();
                    generator.Emit(OpCodes.Newobj, constructor);
                    generator.Emit(OpCodes.Ret);
#if NET5_0_OR_GREATER
                    Factory = dynamicMethod.CreateDelegate<Func<T>>();
#else
                    Factory = (Func<T>)dynamicMethod.CreateDelegate(typeof(Func<T>));
#endif
                    break;
#endif
                default:
                    Factory = () => Activator.CreateInstance<T>();
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining
#if NET5_0_OR_GREATER
            | MethodImplOptions.AggressiveOptimization
#endif
            )]
        public static T Create()
        {
            if (useDefault)
                return default!;
            if (Utilities.DynamicCompilationMode == Utilities.DisabledDynamicCompilation)
                return Activator.CreateInstance<T>();
            return Factory();
        }
    }
}
