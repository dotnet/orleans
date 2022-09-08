using System;
using System.Collections.Concurrent;
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
        private static readonly Type[] SerializationConstructorParameterTypes = { typeof(SerializationInfo), typeof(StreamingContext) };
        private readonly Func<Type, object> _createConstructorDelegate = t => GetSerializationConstructorInvoker(t, typeof(object), typeof(Action<object, SerializationInfo, StreamingContext>));
        private readonly ConcurrentDictionary<Type, object> _constructors = new();

        /// <summary>
        /// Determines whether the provided type has a serialization constructor.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns><see langword="true" /> if the provided type has a serialization constructor; otherwise, <see langword="false" />.</returns>
        [SecurityCritical]
        public static bool HasSerializationConstructor(Type type) => GetSerializationConstructor(type) != null;

        [SecurityCritical]
        public Action<object, SerializationInfo, StreamingContext> GetSerializationConstructorDelegate(Type type)
            => (Action<object, SerializationInfo, StreamingContext>)_constructors.GetOrAdd(type, _createConstructorDelegate);

        [SecurityCritical]
        public TConstructor GetSerializationConstructorDelegate<TOwner, TConstructor>() where TConstructor : Delegate
            => (TConstructor)GetSerializationConstructorDelegate(typeof(TOwner), typeof(TConstructor));

        private object GetSerializationConstructorDelegate(Type owner, Type delegateType)
            => _constructors.GetOrAdd(owner, (t, d) => GetSerializationConstructorInvoker(t, t, d), delegateType);

        [SecurityCritical]
        private static ConstructorInfo GetSerializationConstructor(Type type) => type.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                SerializationConstructorParameterTypes,
                null);

        [SecurityCritical]
        private static Delegate GetSerializationConstructorInvoker(Type type, Type owner, Type delegateType)
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