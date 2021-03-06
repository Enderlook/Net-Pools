using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Enderlook.Pools;

internal static class CompilationHelper
{
    public const int DisabledDynamicCompilation = 0;
    public const int SystemLinqExpressions = 1;
    public const int SystemReflectionEmitDynamicMethod = 2;
    public static readonly int DynamicCompilationMode;

    public static readonly ParameterExpression[] EmptyParameters = new ParameterExpression[0];

    static CompilationHelper()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;

        // Check if we are in AOT.
        // We can't just try to compile code dynamically and try/catch an exception because the runtime explodes instead of throwing in Unity WebGL.
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.FullName == "UnityEngine, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null")
            {
#pragma warning disable IL2026 // Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code
                Type? applicationType = assembly.GetType("UnityEngine.Application");
#pragma warning restore IL2026 // Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code
                if (applicationType is null) // The trimmer has removed the type, so think about the worst possible case.
                    goto isDisabled;

                PropertyInfo? platformProperty = applicationType.GetProperty("platform", flags);
                if (platformProperty is null) // The trimmer has removed the property, so think about the worst possible case.
                    goto isDisabled;

                PropertyInfo? isEditorProperty = applicationType.GetProperty("isEditor", flags);
                if (isEditorProperty is null) // The trimmer has removed the property, so think about the worst possible case.
                    goto isDisabled;

                if (platformProperty.GetValue(null)!.ToString() == "WebGLPlayer" && !(bool)isEditorProperty.GetValue(null)!)
                    goto isDisabled;
            }
        }

#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        if (!RuntimeFeature.IsDynamicCodeCompiled)
            goto isDisabled;
#endif

        // Try with different approaches because the current platform may not support some of them.
        try
        {
            Expression.Lambda<Func<object>>(Expression.New(typeof(object)), Array.Empty<ParameterExpression>()).Compile()();
            DynamicCompilationMode = SystemLinqExpressions;
            return;
        }
        catch { }

#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        try
        {
            DynamicMethod dynamicMethod = new("Instantiate", typeof(object), Type.EmptyTypes);
            ILGenerator generator = dynamicMethod.GetILGenerator();
            generator.Emit(OpCodes.Newobj, typeof(object).GetConstructor(Type.EmptyTypes)!);
            generator.Emit(OpCodes.Ret);
#if NET5_0_OR_GREATER
            dynamicMethod.CreateDelegate<Func<object>>()();
#else
            ((Func<object>)dynamicMethod.CreateDelegate(typeof(Func<object>)))();
#endif
            DynamicCompilationMode = SystemReflectionEmitDynamicMethod;
            return;
        }
        catch { }
#endif

    isDisabled:
        DynamicCompilationMode = DisabledDynamicCompilation;
    }
}