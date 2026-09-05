using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Security;

namespace Orleans.Serialization
{
    /// <summary>
    /// Creates delegates for calling ISerializable-conformant constructors.
    /// </summary>
    internal sealed class SerializationConstructorFactory
    {
#if NET5_0_OR_GREATER
        private const DynamicallyAccessedMemberTypes SerializationConstructors =
            DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors;
#endif

        private static readonly Type[] SerializationConstructorParameterTypes = { typeof(SerializationInfo), typeof(StreamingContext) };
        private readonly ConcurrentDictionary<Type, object> _constructors = new();

        /// <summary>
        /// Determines whether the provided type has a serialization constructor.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns><see langword="true" /> if the provided type has a serialization constructor; otherwise, <see langword="false" />.</returns>
        [SecurityCritical]
        public static bool HasSerializationConstructor(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(SerializationConstructors)]
#endif
            Type type)
            => GetSerializationConstructor(type) != null;

        [SecurityCritical]
        public Action<object, SerializationInfo, StreamingContext> GetSerializationConstructorDelegate(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(SerializationConstructors)]
#endif
            Type type)
        {
            if (_constructors.TryGetValue(type, out var existing))
            {
                return (Action<object, SerializationInfo, StreamingContext>)existing;
            }

            var created = GetSerializationConstructorInvoker(
                type,
                typeof(object),
                typeof(Action<object, SerializationInfo, StreamingContext>));
            return (Action<object, SerializationInfo, StreamingContext>)_constructors.GetOrAdd(type, created);
        }

        [SecurityCritical]
        public TConstructor GetSerializationConstructorDelegate<
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(SerializationConstructors)] TOwner,
#else
            TOwner,
#endif
            TConstructor>()
            where TConstructor : Delegate
            => (TConstructor)GetSerializationConstructorDelegate(typeof(TOwner), typeof(TConstructor));

        private object GetSerializationConstructorDelegate(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(SerializationConstructors)]
#endif
            Type owner,
            Type delegateType)
        {
            if (_constructors.TryGetValue(owner, out var existing))
            {
                return existing;
            }

            var created = GetSerializationConstructorInvoker(owner, owner, delegateType);
            return _constructors.GetOrAdd(owner, created);
        }

        [SecurityCritical]
        private static ConstructorInfo? GetSerializationConstructor(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(SerializationConstructors)]
#endif
            Type type)
            => type.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                SerializationConstructorParameterTypes,
                null);

        [SecurityCritical]
        private static Delegate GetSerializationConstructorInvoker(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(SerializationConstructors)]
#endif
            Type type,
            Type owner,
            Type delegateType)
        {
            var constructor = GetSerializationConstructor(type) ?? (typeof(Exception).IsAssignableFrom(type) ? GetSerializationConstructor(typeof(Exception)) : null);
            if (constructor is null)
            {
                throw new SerializationException($"{nameof(ISerializable)} constructor not found on type {type}.");
            }

            Type[] parameterTypes;
            if (owner.IsValueType)
            {
                parameterTypes = new[] { typeof(object), owner.MakeByRefType(), typeof(SerializationInfo), typeof(StreamingContext) };
            }
            else
            {
                parameterTypes = new[] { typeof(object), typeof(object), typeof(SerializationInfo), typeof(StreamingContext) };
            }

            var method = new DynamicMethod($"{type}_serialization_ctor", null, parameterTypes, type, skipVisibility: true);
            var il = method.GetILGenerator();

            // arg0 is unused for better delegate performance (avoids argument shuffling thunk)
            il.Emit(OpCodes.Ldarg_1);
            if (type != owner)
            {
                il.Emit(OpCodes.Castclass, type);
            }

            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Call, constructor);
            il.Emit(OpCodes.Ret);

            return method.CreateDelegate(delegateType);
        }
    }
}
