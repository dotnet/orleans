using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Security;

namespace Orleans.Serialization
{
    /// <summary>
    /// Creates delegates for calling methods marked with serialization attributes.
    /// </summary>
    internal class SerializationCallbacksFactory
    {
        private readonly ConcurrentDictionary<Type, object> _cache = new();
        private readonly Func<Type, object> _factory;

        [SecurityCritical]
        public SerializationCallbacksFactory()
        {
            _factory = CreateTypedCallbacks<object, Action<object, StreamingContext>>;
        }

        [SecurityCritical]
        public SerializationCallbacks<Action<object, StreamingContext>> GetReferenceTypeCallbacks(Type type) => (
            SerializationCallbacks<Action<object, StreamingContext>>)_cache.GetOrAdd(type, _factory);

        [SecurityCritical]
        public SerializationCallbacks<TDelegate> GetValueTypeCallbacks<TOwner, TDelegate>(Type type) => (
            SerializationCallbacks<TDelegate>)_cache.GetOrAdd(type, t => (object)CreateTypedCallbacks<TOwner, TDelegate>(type));

        [SecurityCritical]
        private static SerializationCallbacks<TDelegate> CreateTypedCallbacks<TOwner, TDelegate>(Type type)
        {
            var typeInfo = type.GetTypeInfo();
            var onDeserializing = default(TDelegate);
            var onDeserialized = default(TDelegate);
            var onSerializing = default(TDelegate);
            var onSerialized = default(TDelegate);
            foreach (var method in typeInfo.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var parameterInfos = method.GetParameters();
                if (parameterInfos.Length != 1)
                {
                    continue;
                }

                if (parameterInfos[0].ParameterType != typeof(StreamingContext))
                {
                    continue;
                }

                if (method.GetCustomAttribute<OnDeserializingAttribute>() != null)
                {
                    onDeserializing = GetSerializationMethod<TOwner, TDelegate>(typeInfo, method);
                }

                if (method.GetCustomAttribute<OnDeserializedAttribute>() != null)
                {
                    onDeserialized = GetSerializationMethod<TOwner, TDelegate>(typeInfo, method);
                }

                if (method.GetCustomAttribute<OnSerializingAttribute>() != null)
                {
                    onSerializing = GetSerializationMethod<TOwner, TDelegate>(typeInfo, method);
                }

                if (method.GetCustomAttribute<OnSerializedAttribute>() != null)
                {
                    onSerialized = GetSerializationMethod<TOwner, TDelegate>(typeInfo, method);
                }
            }

            return new SerializationCallbacks<TDelegate>(onDeserializing, onDeserialized, onSerializing, onSerialized);
        }

        [SecurityCritical]
        private static TDelegate GetSerializationMethod<TOwner, TDelegate>(Type type, MethodInfo callbackMethod)
        {
            Type[] callbackParameterTypes;
            if (typeof(TOwner).IsValueType)
            {
                callbackParameterTypes = new[] { typeof(TOwner).MakeByRefType(), typeof(StreamingContext) };
            }
            else
            {
                callbackParameterTypes = new[] { typeof(object), typeof(StreamingContext) };
            }

            var method = new DynamicMethod($"{callbackMethod.Name}_Trampoline", null, callbackParameterTypes, type, skipVisibility: true);
            var il = method.GetILGenerator();

            il.Emit(OpCodes.Ldarg_0);
            if (type != typeof(TOwner))
            {
                il.Emit(OpCodes.Castclass, type);
            }

            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, callbackMethod);
            il.Emit(OpCodes.Ret);

            object result = method.CreateDelegate(typeof(TDelegate));
            return (TDelegate)result;
        }

        public class SerializationCallbacks<TDelegate>
        {
            public SerializationCallbacks(
                TDelegate onDeserializing,
                TDelegate onDeserialized,
                TDelegate onSerializing,
                TDelegate onSerialized)
            {
                OnDeserializing = onDeserializing;
                OnDeserialized = onDeserialized;
                OnSerializing = onSerializing;
                OnSerialized = onSerialized;
            }

            public TDelegate OnDeserializing { get; }
            public TDelegate OnDeserialized { get; }
            public TDelegate OnSerializing { get; }
            public TDelegate OnSerialized { get; }
        }
    }
}