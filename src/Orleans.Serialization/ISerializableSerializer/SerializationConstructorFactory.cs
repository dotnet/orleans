using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
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

            var constructor = GetRequiredSerializationConstructor(type);
            Action<object, SerializationInfo, StreamingContext> created;
            if (RuntimeFeature.IsDynamicCodeSupported)
            {
                created = (Action<object, SerializationInfo, StreamingContext>)GetSerializationConstructorInvoker(
                    constructor, type, typeof(object), typeof(Action<object, SerializationInfo, StreamingContext>));
            }
            else
            {
                created = CreateNativeConstructor(constructor);
            }

            return (Action<object, SerializationInfo, StreamingContext>)_constructors.GetOrAdd(type, created);
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
            if (_constructors.TryGetValue(owner, out var existing))
            {
                return (ValueTypeSerializer<TOwner>.ValueConstructor)existing;
            }

            var constructor = GetRequiredSerializationConstructor(owner);
            ValueTypeSerializer<TOwner>.ValueConstructor created;
            if (RuntimeFeature.IsDynamicCodeSupported)
            {
                created = (ValueTypeSerializer<TOwner>.ValueConstructor)GetSerializationConstructorInvoker(
                    constructor, owner, owner, typeof(ValueTypeSerializer<TOwner>.ValueConstructor));
            }
            else
            {
                created = (ref TOwner value, SerializationInfo info, StreamingContext context) =>
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
            }

            return (ValueTypeSerializer<TOwner>.ValueConstructor)_constructors.GetOrAdd(owner, created);
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

        private static Action<object, SerializationInfo, StreamingContext> CreateNativeConstructor(ConstructorInfo constructor)
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

        [SecurityCritical]
#if NET7_0_OR_GREATER
        [RequiresDynamicCode("Serialization constructor trampolines require runtime code generation.")]
#endif
        private static Delegate GetSerializationConstructorInvoker(ConstructorInfo constructor, Type type, Type owner, Type delegateType)
        {
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
                il.Emit(type.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, type);
            }

            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Call, constructor);
            il.Emit(OpCodes.Ret);

            return method.CreateDelegate(delegateType);
        }
    }
}
