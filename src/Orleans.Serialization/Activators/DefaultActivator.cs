using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Activators
{
    internal abstract class DefaultActivator<
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
#endif
        T> : IActivator<T>
    {
        private static readonly Func<T>? DefaultConstructorFunction = Init();
        protected readonly Func<T>? Constructor = DefaultConstructorFunction;
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
#endif
        protected readonly Type Type = typeof(T);

        private static Func<T>? Init()
        {
            var ctor = typeof(T).GetConstructor(Type.EmptyTypes);
            if (ctor is null)
                return null;

            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                return () => (T)ctor.Invoke(BindingFlags.DoNotWrapExceptions, binder: null, parameters: null, culture: null);
            }

            var method = new DynamicMethod(nameof(DefaultActivator<T>), typeof(T), new[] { typeof(object) });
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Newobj, ctor);
            il.Emit(OpCodes.Ret);
            return (Func<T>)method.CreateDelegate(typeof(Func<T>));
        }

        public abstract T Create();
    }

    internal sealed class DefaultReferenceTypeActivator<
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
#endif
        T> : DefaultActivator<T> where T : class
    {
        public override T Create()
            => Constructor is { } ctor
                ? ctor()
                : Unsafe.As<T>(RuntimeHelpers.GetUninitializedObject(Type));
    }

    internal sealed class DefaultValueTypeActivator<
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
#endif
        T> : DefaultActivator<T> where T : struct
    {
        public override T Create()
            => Constructor is { } ctor
                ? ctor()
                : (T)RuntimeHelpers.GetUninitializedObject(Type);
    }
}
