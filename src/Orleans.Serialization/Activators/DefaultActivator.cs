using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Orleans.Serialization.Activators
{
    internal abstract class DefaultActivator<
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]

#endif
    T> : IActivator<T>
    {
        private static readonly bool HasDefaultConstructor = typeof(T).GetConstructor(Type.EmptyTypes) is not null;

        public T Create()
        {
            if (!HasDefaultConstructor)
            {
                return (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            }

            try
            {
                return System.Activator.CreateInstance<T>();
            }
            catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
            {
                ExceptionDispatchInfo.Capture(inner).Throw();
                throw;
            }
        }
    }

    internal sealed class DefaultReferenceTypeActivator<
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]

#endif
    T> : DefaultActivator<T> where T : class
    {
    }

    internal sealed class DefaultValueTypeActivator<
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]

#endif
    T> : DefaultActivator<T> where T : struct
    {
    }
}
