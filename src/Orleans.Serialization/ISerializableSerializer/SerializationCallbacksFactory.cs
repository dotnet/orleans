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
    internal sealed class SerializationCallbacksFactory
    {
        private readonly ConcurrentDictionary<Type, object> _cache = new();
        private readonly Func<Type, object> _factory = t => CreateTypedCallbacks<Action<object, StreamingContext>>(t, typeof(object));

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
        public SerializationCallbacks<TDelegate> GetValueTypeCallbacks<TOwner, TDelegate>(Type type) where TOwner : struct where TDelegate : Delegate
            => GetValueTypeCallbacks<TDelegate>(type, typeof(TOwner));

        private SerializationCallbacks<TDelegate> GetValueTypeCallbacks<TDelegate>(Type type, Type owner) where TDelegate : Delegate
            => (SerializationCallbacks<TDelegate>)_cache.GetOrAdd(type, (t, o) => CreateTypedCallbacks<TDelegate>(t, o), owner);

        [SecurityCritical]
        private static SerializationCallbacks<TDelegate> CreateTypedCallbacks<TDelegate>(Type type, Type owner) where TDelegate : Delegate
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

                if (method.IsDefined(typeof(OnDeserializingAttribute), false))
                {
                    onDeserializing = (TDelegate)GetSerializationMethod(type, method, owner).CreateDelegate(typeof(TDelegate));
                }

                if (method.IsDefined(typeof(OnDeserializedAttribute), false))
                {
                    onDeserialized = (TDelegate)GetSerializationMethod(type, method, owner).CreateDelegate(typeof(TDelegate));
                }

                if (method.IsDefined(typeof(OnSerializingAttribute), false))
                {
                    onSerializing = (TDelegate)GetSerializationMethod(type, method, owner).CreateDelegate(typeof(TDelegate));
                }

                if (method.IsDefined(typeof(OnSerializedAttribute), false))
                {
                    onSerialized = (TDelegate)GetSerializationMethod(type, method, owner).CreateDelegate(typeof(TDelegate));
                }
            }

            return new SerializationCallbacks<TDelegate>(onDeserializing, onDeserialized, onSerializing, onSerialized);
        }

        [SecurityCritical]
        private static DynamicMethod GetSerializationMethod(Type type, MethodInfo callbackMethod, Type owner)
        {
            Type[] callbackParameterTypes;
            if (owner.IsValueType)
            {
                callbackParameterTypes = new[] { typeof(object), owner.MakeByRefType(), typeof(StreamingContext) };
            }
            else
            {
                callbackParameterTypes = new[] { typeof(object), typeof(object), typeof(StreamingContext) };
            }

            var method = new DynamicMethod($"{callbackMethod.Name}_Trampoline", null, callbackParameterTypes, type, skipVisibility: true);
            var il = method.GetILGenerator();

            // arg0 is unused for better delegate performance (avoids argument shuffling thunk)
            il.Emit(OpCodes.Ldarg_1);
            if (type != owner)
            {
                il.Emit(OpCodes.Castclass, type);
            }

            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Callvirt, callbackMethod);
            il.Emit(OpCodes.Ret);

            return method;
        }

        /// <summary>
        /// Serialization callbacks.
        /// </summary>
        /// <typeparam name="TDelegate">The delegate type for each callback.</typeparam>
        public sealed class SerializationCallbacks<TDelegate>
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
            public readonly TDelegate OnDeserializing;

            /// <summary>
            /// Gets the callback invoked once a value has been deserialized.
            /// </summary>
            public readonly TDelegate OnDeserialized;

            /// <summary>
            /// Gets the callback invoked during serialization.
            /// </summary>
            /// <value>The on serializing.</value>
            public readonly TDelegate OnSerializing;

            /// <summary>
            /// Gets the callback invoked once a value has been serialized.
            /// </summary>
            public readonly TDelegate OnSerialized;
        }
    }
}