using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        private readonly ConcurrentDictionary<(Type Owner, Type DelegateType), Delegate> _constructors = new();

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
            var key = (type, typeof(Action<object, SerializationInfo, StreamingContext>));
            if (_constructors.TryGetValue(key, out var existing))
            {
                return (Action<object, SerializationInfo, StreamingContext>)existing;
            }

            var constructor = GetRequiredSerializationConstructor(type);
            var created = CreateConstructor(constructor);

            return (Action<object, SerializationInfo, StreamingContext>)_constructors.GetOrAdd(key, created);
        }

        [SecurityCritical]
        public ValueTypeSerializer<TOwner>.ValueConstructor GetSerializationConstructorDelegate<
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(SerializationConstructors)]
#endif
        TOwner>()
            where TOwner : struct
        {
            var owner = typeof(TOwner);
            var key = (owner, typeof(ValueTypeSerializer<TOwner>.ValueConstructor));
            if (_constructors.TryGetValue(key, out var existing))
            {
                return (ValueTypeSerializer<TOwner>.ValueConstructor)existing;
            }

            var constructor = GetRequiredSerializationConstructor(owner);
            ValueTypeSerializer<TOwner>.ValueConstructor created = (ref TOwner value, SerializationInfo info, StreamingContext context) =>
            {
                object boxed = value;
                try
                {
                    constructor.Invoke(boxed, BindingFlags.DoNotWrapExceptions, null, new object[] { info, context }, null);
                }
                finally
                {
                    // Preserve constructor mutations even when it throws.
                    value = (TOwner)boxed;
                }
            };

            return (ValueTypeSerializer<TOwner>.ValueConstructor)_constructors.GetOrAdd(key, created);
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
        private static ConstructorInfo GetRequiredSerializationConstructor(
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(SerializationConstructors)]
#endif
            Type type)
        {
            var constructor = GetSerializationConstructor(type) ?? (typeof(Exception).IsAssignableFrom(type) ? GetSerializationConstructor(typeof(Exception)) : null);
            if (constructor is null)
            {
                throw new SerializationException($"{nameof(ISerializable)} constructor not found on type {type}.");
            }

            return constructor;
        }

        private static Action<object, SerializationInfo, StreamingContext> CreateConstructor(ConstructorInfo constructor)
        {
#if NET8_0_OR_GREATER
            if (constructor.DeclaringType == typeof(Exception))
            {
                return (value, info, context) => InitializeException((Exception)value, info, context);
            }
#endif

            return (value, info, context) =>
                constructor.Invoke(value, BindingFlags.DoNotWrapExceptions, null, new object[] { info, context }, null);
        }

#if NET8_0_OR_GREATER
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = ".ctor")]
        private static extern void InitializeException(Exception value, SerializationInfo info, StreamingContext context);
#endif
    }
}
