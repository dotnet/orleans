using System;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Activators
{
    internal abstract class DefaultActivator<T> : IActivator<T>
    {
        private static readonly Func<T> DefaultConstructorFunction = Init();
        protected readonly Func<T> Constructor = DefaultConstructorFunction;
        protected readonly Type Type = typeof(T);

        private static Func<T> Init()
        {
            var ctor = typeof(T).GetConstructor(Type.EmptyTypes);
            if (ctor is null)
                return null;

            var method = new DynamicMethod(nameof(DefaultActivator<T>), typeof(T), new[] { typeof(object) });
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Newobj, ctor);
            il.Emit(OpCodes.Ret);
            return (Func<T>)method.CreateDelegate(typeof(Func<T>));
        }

        public abstract T Create();
    }

    internal sealed class DefaultReferenceTypeActivator<T> : DefaultActivator<T> where T : class
    {
        public override T Create()
            => Constructor is { } ctor
                ? ctor()
                : Unsafe.As<T>(RuntimeHelpers.GetUninitializedObject(Type));
    }

    internal sealed class DefaultValueTypeActivator<T> : DefaultActivator<T> where T : struct
    {
        public override T Create()
            => Constructor is { } ctor
                ? ctor()
                : (T)RuntimeHelpers.GetUninitializedObject(Type);
    }
}