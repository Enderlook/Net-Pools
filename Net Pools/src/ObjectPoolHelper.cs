using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Enderlook.Pools
{
    internal static class ObjectPoolHelper<T> where T : class
    {
        public static readonly Func<T> Factory;

        static ObjectPoolHelper()
        {
            // TODO: In .NET 7 Activator.CreateFactory<T>() may be added https://github.com/dotnet/runtime/issues/36194.

            ConstructorInfo? constructor = typeof(T).GetConstructor(Type.EmptyTypes);
            if (constructor is null)
            {
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

                    DynamicMethod dynamicMethod = new DynamicMethod("Instantiate", typeof(T), Type.EmptyTypes);
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Create()
            => Utilities.DynamicCompilationMode == Utilities.DisabledDynamicCompilation ? Activator.CreateInstance<T>() : Factory();
    }
}
