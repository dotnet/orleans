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
    internal class SerializationConstructorFactory
    {
        private static readonly Type[] SerializationConstructorParameterTypes = { typeof(SerializationInfo), typeof(StreamingContext) };
        private readonly Func<Type, object> _createConstructorDelegate;
        private readonly ConcurrentDictionary<Type, object> _constructors = new();

        [SecurityCritical]
        public SerializationConstructorFactory()
        {
            _createConstructorDelegate =
                GetSerializationConstructorInvoker<object, Action<object, SerializationInfo, StreamingContext>>;
        }

        [SecurityCritical]
        public static bool HasSerializationConstructor(Type type) => GetSerializationConstructor(type) != null;

        [SecurityCritical]
        public Action<object, SerializationInfo, StreamingContext> GetSerializationConstructorDelegate(Type type) => (Action<object, SerializationInfo, StreamingContext>)_constructors.GetOrAdd(
                type,
                _createConstructorDelegate);

        [SecurityCritical]
        public TConstructor GetSerializationConstructorDelegate<TOwner, TConstructor>() => (TConstructor)_constructors.GetOrAdd(
                typeof(TOwner),
                type => (object)GetSerializationConstructorInvoker<TOwner, TConstructor>(type));

        [SecurityCritical]
        private static ConstructorInfo GetSerializationConstructor(Type type) => type.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                SerializationConstructorParameterTypes,
                null);

        [SecurityCritical]
        private static TConstructor GetSerializationConstructorInvoker<TOwner, TConstructor>(Type type)
        {
            var constructor = GetSerializationConstructor(type) ?? (typeof(Exception).IsAssignableFrom(type) ? GetSerializationConstructor(typeof(Exception)) : null);
            if (constructor is null)
            {
                throw new SerializationException($"{nameof(ISerializable)} constructor not found on type {type}.");
            }

            Type[] parameterTypes;
            if (typeof(TOwner).IsValueType)
            {
                parameterTypes = new[] { typeof(TOwner).MakeByRefType(), typeof(SerializationInfo), typeof(StreamingContext) };
            }
            else
            {
                parameterTypes = new[] { typeof(object), typeof(SerializationInfo), typeof(StreamingContext) };
            }

            var method = new DynamicMethod($"{type}_serialization_ctor", null, parameterTypes, typeof(TOwner), skipVisibility: true);
            var il = method.GetILGenerator();

            il.Emit(OpCodes.Ldarg_0);
            if (type != typeof(TOwner))
            {
                il.Emit(OpCodes.Castclass, type);
            }

            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, constructor);
            il.Emit(OpCodes.Ret);

            object result = method.CreateDelegate(typeof(TConstructor));
            return (TConstructor)result;
        }
    }
}