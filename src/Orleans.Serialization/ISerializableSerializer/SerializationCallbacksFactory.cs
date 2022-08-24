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

        /// <summary>
        /// Initializes a new instance of the <see cref="SerializationCallbacksFactory"/> class.
        /// </summary>
        [SecurityCritical]
        public SerializationCallbacksFactory()
        {
            _factory = CreateTypedCallbacks<object, Action<object, StreamingContext>>;
        }

        /// <summary>
        /// Gets serialization callbacks for reference types.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>Serialization callbacks.</returns>
        [SecurityCritical]
        public SerializationCallbacks<Action<object, StreamingContext>> GetReferenceTypeCallbacks(Type type) => (
            SerializationCallbacks<Action<object, StreamingContext>>)_cache.GetOrAdd(type, _factory);

        /// <summary>
        /// Gets serialization callbacks for value types.
        /// </summary>
        /// <typeparam name="TOwner">The declaring type.</typeparam>
        /// <typeparam name="TDelegate">The delegate type.</typeparam>
        /// <param name="type">The type.</param>
        /// <returns>Serialization callbacks.</returns>
        [SecurityCritical]
        public SerializationCallbacks<TDelegate> GetValueTypeCallbacks<TOwner, TDelegate>(Type type) => (
            SerializationCallbacks<TDelegate>)_cache.GetOrAdd(type, t => CreateTypedCallbacks<TOwner, TDelegate>(t));

        [SecurityCritical]
        private static SerializationCallbacks<TDelegate> CreateTypedCallbacks<TOwner, TDelegate>(Type type)
        {
            var onDeserializing = default(TDelegate);
            var onDeserialized = default(TDelegate);
            var onSerializing = default(TDelegate);
            var onSerialized = default(TDelegate);
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
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
                    onDeserializing = GetSerializationMethod<TOwner, TDelegate>(type, method);
                }

                if (method.GetCustomAttribute<OnDeserializedAttribute>() != null)
                {
                    onDeserialized = GetSerializationMethod<TOwner, TDelegate>(type, method);
                }

                if (method.GetCustomAttribute<OnSerializingAttribute>() != null)
                {
                    onSerializing = GetSerializationMethod<TOwner, TDelegate>(type, method);
                }

                if (method.GetCustomAttribute<OnSerializedAttribute>() != null)
                {
                    onSerialized = GetSerializationMethod<TOwner, TDelegate>(type, method);
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

        /// <summary>
        /// Serialization callbacks.
        /// </summary>
        /// <typeparam name="TDelegate">The delegate type for each callback.</typeparam>
        public class SerializationCallbacks<TDelegate>
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="SerializationCallbacks{TDelegate}"/> class.
            /// </summary>
            /// <param name="onDeserializing">The callback invoked during deserialization.</param>
            /// <param name="onDeserialized">The callback invoked once a value is deserialized.</param>
            /// <param name="onSerializing">The callback invoked during serialization.</param>
            /// <param name="onSerialized">The callback invoked once a value is serialized.</param>
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

            /// <summary>
            /// Gets the callback invoked while deserializing.
            /// </summary>
            public TDelegate OnDeserializing { get; }

            /// <summary>
            /// Gets the callback invoked once a value has been deserialized.
            /// </summary>
            public TDelegate OnDeserialized { get; }

            /// <summary>
            /// Gets the callback invoked during serialization.
            /// </summary>
            /// <value>The on serializing.</value>
            public TDelegate OnSerializing { get; }

            /// <summary>
            /// Gets the callback invoked once a value has been serialized. 
            /// </summary>
            public TDelegate OnSerialized { get; }
        }
    }
}