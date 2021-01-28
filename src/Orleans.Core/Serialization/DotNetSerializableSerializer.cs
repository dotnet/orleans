using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using Orleans.Runtime;
using Orleans.Utilities;

namespace Orleans.Serialization
{
    /// <summary>
    /// Serializer for types which implement <see cref="ISerializable"/>, including the required constructor.
    /// </summary>
    internal sealed class DotNetSerializableSerializer : IKeyedSerializer
    {
        private readonly IFormatterConverter _formatterConverter = new FormatterConverter();
        private readonly TypeInfo _serializableType = typeof(ISerializable).GetTypeInfo();
        private readonly SerializationConstructorFactory _constructorFactory = new SerializationConstructorFactory();
        private readonly SerializationCallbacksFactory _serializationCallbacks = new SerializationCallbacksFactory();
        private readonly ValueTypeSerializerFactory _valueTypeSerializerFactory;
        private readonly ITypeResolver _typeResolver;
        private readonly ObjectSerializer _objectSerializer;

        public DotNetSerializableSerializer(ITypeResolver typeResolver)
        {
            _typeResolver = typeResolver;
            _objectSerializer = new ObjectSerializer(_constructorFactory, _serializationCallbacks, _formatterConverter);
            _valueTypeSerializerFactory = new ValueTypeSerializerFactory(_constructorFactory, _serializationCallbacks, _formatterConverter);
        }

        /// <inheritdoc />
        public KeyedSerializerId SerializerId => KeyedSerializerId.ISerializableSerializer;

        /// <inheritdoc />
        public bool IsSupportedType(Type itemType) => _serializableType.IsAssignableFrom(itemType) &&
                                                      (_constructorFactory.GetSerializationConstructor(itemType) != null || typeof(Exception).IsAssignableFrom(itemType));

        /// <inheritdoc />
        public object DeepCopy(object source, ICopyContext context)
        {
            var type = source.GetType();
            if (type.IsValueType)
            {
                var serializer = _valueTypeSerializerFactory.GetSerializer(type);
                return serializer.DeepCopy(source, context);
            }

            return _objectSerializer.DeepCopy(source, context);
        }

        /// <inheritdoc />
        public void Serialize(object item, ISerializationContext context, Type expectedType)
        {
            var type = item.GetType();
            if (typeof(Exception).IsAssignableFrom(type))
            {
                context.StreamWriter.Write((byte)SerializerTypeToken.Exception);
                var typeName = RuntimeTypeNameFormatter.Format(type);
                SerializationManager.SerializeInner(typeName, context);
                _objectSerializer.Serialize(item, context);
            }
            else
            {
                context.StreamWriter.Write((byte)SerializerTypeToken.Other);
                SerializationManager.SerializeInner(type, context);
                if (type.IsValueType)
                {
                    var serializer = _valueTypeSerializerFactory.GetSerializer(type);
                    serializer.Serialize(item, context);
                }
                else
                {
                    _objectSerializer.Serialize(item, context);
                }
            }
        }

        /// <inheritdoc />
        public object Deserialize(Type expectedType, IDeserializationContext context)
        {
            var startOffset = context.CurrentObjectOffset;
            var token = (SerializerTypeToken)context.StreamReader.ReadByte();
            if (token == SerializerTypeToken.Exception)
            {
                var typeName = SerializationManager.DeserializeInner<string>(context);
                if (!_typeResolver.TryResolveType(typeName, out var type))
                {
                    // Deserialize into a fallback type for unknown exceptions
                    // This means that missing fields will not be represented.
                    var result = (UnavailableExceptionFallbackException)_objectSerializer.Deserialize(typeof(UnavailableExceptionFallbackException), startOffset, context);
                    result.ExceptionType = typeName;
                    return result;
                }

                return _objectSerializer.Deserialize(type, startOffset, context);
            }
            else
            {
                var type = SerializationManager.DeserializeInner<Type>(context);
                if (type.IsValueType)
                {
                    var serializer = _valueTypeSerializerFactory.GetSerializer(type);
                    return serializer.Deserialize(type, startOffset, context);
                }

                return _objectSerializer.Deserialize(type, startOffset, context);
            }
        }

        /// <summary>
        /// Serializer for ISerializable reference types.
        /// </summary>
        internal class ObjectSerializer
        {
            private readonly IFormatterConverter _formatterConverter;
            private readonly SerializationConstructorFactory _constructorFactory;
            private readonly SerializationCallbacksFactory _serializationCallbacks;

            public ObjectSerializer(
                SerializationConstructorFactory constructorFactory,
                SerializationCallbacksFactory serializationCallbacks,
                IFormatterConverter formatterConverter)
            {
                _constructorFactory = constructorFactory;
                _serializationCallbacks = serializationCallbacks;
                _formatterConverter = formatterConverter;
            }

            /// <inheritdoc />
            public object DeepCopy(object source, ICopyContext context)
            {
                var type = source.GetType();
                var callbacks = _serializationCallbacks.GetReferenceTypeCallbacks(type);
                var serializable = (ISerializable)source;
                var result = FormatterServices.GetUninitializedObject(type);
                context.RecordCopy(source, result);

                // Shallow-copy the object into the serialization info.
                var originalInfo = new SerializationInfo(type, _formatterConverter);
                var streamingContext = new StreamingContext(StreamingContextStates.All, context);
                callbacks.OnSerializing?.Invoke(source, streamingContext);
                serializable.GetObjectData(originalInfo, streamingContext);

                // Deep-copy the serialization info.
                var copyInfo = new SerializationInfo(type, _formatterConverter);
                foreach (var item in originalInfo)
                {
                    copyInfo.AddValue(item.Name, SerializationManager.DeepCopyInner(item.Value, context));
                }
                callbacks.OnSerialized?.Invoke(source, streamingContext);
                callbacks.OnDeserializing?.Invoke(result, streamingContext);

                // Shallow-copy the serialization info into the result.
                var constructor = _constructorFactory.GetSerializationConstructorDelegate(type);
                constructor(result, copyInfo, streamingContext);
                callbacks.OnDeserialized?.Invoke(result, streamingContext);
                if (result is IDeserializationCallback callback)
                {
                    callback.OnDeserialization(context);
                }

                return result;
            }

            /// <inheritdoc />
            public void Serialize(object item, ISerializationContext context)
            {
                var type = item.GetType();
                var callbacks = _serializationCallbacks.GetReferenceTypeCallbacks(type);
                var info = new SerializationInfo(type, _formatterConverter);
                var streamingContext = new StreamingContext(StreamingContextStates.All, context);
                callbacks.OnSerializing?.Invoke(item, streamingContext);
                ((ISerializable)item).GetObjectData(info, streamingContext);

                SerializationManager.SerializeInner(info.MemberCount, context);
                foreach (var entry in info)
                {
                    SerializationManager.SerializeInner(entry.Name, context);
                    var fieldType = entry.Value?.GetType();
                    SerializationManager.SerializeInner(fieldType, context);
                    SerializationManager.SerializeInner(entry.Value, context, fieldType);
                }

                callbacks.OnSerialized?.Invoke(item, streamingContext);
            }

            /// <inheritdoc />
            public object Deserialize(Type type, int startOffset, IDeserializationContext context)
            {
                var callbacks = _serializationCallbacks.GetReferenceTypeCallbacks(type);
                var result = FormatterServices.GetUninitializedObject(type);
                context.RecordObject(result, startOffset);

                var memberCount = SerializationManager.DeserializeInner<int>(context);

                var info = new SerializationInfo(type, _formatterConverter);
                var streamingContext = new StreamingContext(StreamingContextStates.All, context);
                callbacks.OnDeserializing?.Invoke(result, streamingContext);

                for (var i = 0; i < memberCount; i++)
                {
                    var name = SerializationManager.DeserializeInner<string>(context);
                    var fieldType = SerializationManager.DeserializeInner<Type>(context);
                    var value = SerializationManager.DeserializeInner(fieldType, context);
                    info.AddValue(name, value);
                }

                var constructor = _constructorFactory.GetSerializationConstructorDelegate(type);
                constructor(result, info, streamingContext);
                callbacks.OnDeserialized?.Invoke(result, streamingContext);
                if (result is IDeserializationCallback callback)
                {
                    callback.OnDeserialization(context);
                }

                return result;
            }
        }

        /// <summary>
        /// Serializer for ISerializable value types.
        /// </summary>
        internal abstract class ValueTypeSerializer
        {
            public abstract object Deserialize(Type type, int startOffset, IDeserializationContext context);
            public abstract void Serialize(object item, ISerializationContext context);
            public abstract object DeepCopy(object source, ICopyContext context);
        }

        /// <summary>
        /// Serializer for ISerializable value types.
        /// </summary>
        /// <typeparam name="T">The type which this serializer can serialize.</typeparam>
        internal class ValueTypeSerializer<T> : ValueTypeSerializer where T : struct, ISerializable
        {
            public delegate void ValueConstructor(ref T value, SerializationInfo info, StreamingContext context);
            public delegate void SerializationCallback(ref T value, StreamingContext context);

            private readonly ValueConstructor _constructor;
            private readonly SerializationCallbacksFactory.SerializationCallbacks<SerializationCallback> _callbacks;
            private readonly IFormatterConverter _formatterConverter;

            public ValueTypeSerializer(
                ValueConstructor constructor,
                SerializationCallbacksFactory.SerializationCallbacks<SerializationCallback> callbacks,
                IFormatterConverter formatterConverter)
            {
                _constructor = constructor;
                _callbacks = callbacks;
                _formatterConverter = formatterConverter;
            }

            public override object Deserialize(Type type, int startOffset, IDeserializationContext context)
            {
                var result = default(T);
                var memberCount = SerializationManager.DeserializeInner<int>(context);

                var info = new SerializationInfo(type, _formatterConverter);
                var streamingContext = new StreamingContext(StreamingContextStates.All, context);
                _callbacks.OnDeserializing?.Invoke(ref result, streamingContext);

                for (var i = 0; i < memberCount; i++)
                {
                    var name = SerializationManager.DeserializeInner<string>(context);
                    var fieldType = SerializationManager.DeserializeInner<Type>(context);
                    var value = SerializationManager.DeserializeInner(fieldType, context);
                    info.AddValue(name, value);
                }

                _constructor(ref result, info, streamingContext);
                _callbacks.OnDeserialized?.Invoke(ref result, streamingContext);
                if (result is IDeserializationCallback callback)
                {
                    callback.OnDeserialization(context);
                }

                return result;
            }

            public override void Serialize(object item, ISerializationContext context)
            {
                var localItem = (T)item;
                var type = item.GetType();
                var info = new SerializationInfo(type, _formatterConverter);
                var streamingContext = new StreamingContext(StreamingContextStates.All, context);
                _callbacks.OnSerializing?.Invoke(ref localItem, streamingContext);
                localItem.GetObjectData(info, streamingContext);

                SerializationManager.SerializeInner(info.MemberCount, context);
                foreach (var entry in info)
                {
                    SerializationManager.SerializeInner(entry.Name, context);
                    var fieldType = entry.Value?.GetType();
                    SerializationManager.SerializeInner(fieldType, context);
                    SerializationManager.SerializeInner(entry.Value, context, fieldType);
                }

                _callbacks.OnSerialized?.Invoke(ref localItem, streamingContext);
            }

            public override object DeepCopy(object source, ICopyContext context)
            {
                var localSource = (T)source;
                var type = source.GetType();
                var result = default(T);

                // Shallow-copy the object into the serialization info.
                var originalInfo = new SerializationInfo(type, _formatterConverter);
                var streamingContext = new StreamingContext(StreamingContextStates.All, context);
                _callbacks.OnSerializing?.Invoke(ref localSource, streamingContext);
                localSource.GetObjectData(originalInfo, streamingContext);

                // Deep-copy the serialization info.
                var copyInfo = new SerializationInfo(type, _formatterConverter);
                foreach (var item in originalInfo)
                {
                    copyInfo.AddValue(item.Name, SerializationManager.DeepCopyInner(item.Value, context));
                }

                _callbacks.OnSerialized?.Invoke(ref localSource, streamingContext);
                _callbacks.OnDeserializing?.Invoke(ref localSource, streamingContext);

                // Shallow-copy the serialization info into the result.
                _constructor(ref result, copyInfo, streamingContext);
                _callbacks.OnDeserialized?.Invoke(ref result, streamingContext);
                if (result is IDeserializationCallback callback)
                {
                    callback.OnDeserialization(context);
                }

                return result;
            }
        }

        /// <summary>
        /// Creates <see cref="ValueTypeSerializer"/> instances for value types.
        /// </summary>
        internal class ValueTypeSerializerFactory
        {
            private readonly SerializationConstructorFactory _constructorFactory;
            private readonly SerializationCallbacksFactory _callbacksFactory;
            private readonly IFormatterConverter _formatterConverter;
            private readonly Func<Type, ValueTypeSerializer> _createSerializerDelegate;

            private readonly ConcurrentDictionary<Type, ValueTypeSerializer> _serializers = new ConcurrentDictionary<Type, ValueTypeSerializer>();

            private readonly MethodInfo _createTypedSerializerMethodInfo = typeof(ValueTypeSerializerFactory).GetMethod(
                nameof(CreateTypedSerializer),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            public ValueTypeSerializerFactory(
                SerializationConstructorFactory constructorFactory,
                SerializationCallbacksFactory callbacksFactory,
                IFormatterConverter formatterConverter)
            {
                _constructorFactory = constructorFactory;
                _callbacksFactory = callbacksFactory;
                _formatterConverter = formatterConverter;
                _createSerializerDelegate = type => (ValueTypeSerializer) 
                    _createTypedSerializerMethodInfo.MakeGenericMethod(type).Invoke(this, null);
            }

            public ValueTypeSerializer GetSerializer(Type type)
            {
                return _serializers.GetOrAdd(type, _createSerializerDelegate);
            }

            private ValueTypeSerializer CreateTypedSerializer<T>() where T : struct, ISerializable
            {
                var constructor = _constructorFactory.GetSerializationConstructorDelegate<T, ValueTypeSerializer<T>.ValueConstructor>();
                var callbacks =
                    _callbacksFactory.GetValueTypeCallbacks<T, ValueTypeSerializer<T>.SerializationCallback>(typeof(T));
                return new ValueTypeSerializer<T>(constructor, callbacks, _formatterConverter);
            }
        }

        /// <summary>
        /// Creates delegates for calling ISerializable-conformant constructors.
        /// </summary>
        internal class SerializationConstructorFactory
        {
            private static readonly Type[] SerializationConstructorParameterTypes = { typeof(SerializationInfo), typeof(StreamingContext) };

            private readonly Func<Type, object> _createConstructorDelegate;

            private readonly ConcurrentDictionary<Type, object> _constructors = new ConcurrentDictionary<Type, object>();

            public SerializationConstructorFactory()
            {
                _createConstructorDelegate = 
                    GetSerializationConstructorInvoker<object, Action<object, SerializationInfo, StreamingContext>>;
            }

            public Action<object, SerializationInfo, StreamingContext> GetSerializationConstructorDelegate(Type type)
            {
                return (Action<object, SerializationInfo, StreamingContext>)_constructors.GetOrAdd(
                    type,
                    _createConstructorDelegate);
            }

            public TConstructor GetSerializationConstructorDelegate<TOwner, TConstructor>()
            {
                return (TConstructor) _constructors.GetOrAdd(
                    typeof(TOwner),
                    type => (object) GetSerializationConstructorInvoker<TOwner, TConstructor>(type));
            }

            public ConstructorInfo GetSerializationConstructor(Type type)
            {
                return type.GetConstructor(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    SerializationConstructorParameterTypes,
                    null);
            }

            private TConstructor GetSerializationConstructorInvoker<TOwner, TConstructor>(Type type)
            {
                var constructor = GetSerializationConstructor(type) ?? (typeof(Exception).IsAssignableFrom(type) ? GetSerializationConstructor(typeof(Exception)) : null);
                if (constructor == null) throw new SerializationException($"{nameof(ISerializable)} constructor not found on type {type}.");

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

        /// <summary>
        /// Creates delegates for calling methods marked with serialization attributes.
        /// </summary>
        internal class SerializationCallbacksFactory
        {
            private readonly ConcurrentDictionary<Type, object> _cache = new ConcurrentDictionary<Type, object>();
            private readonly Func<Type, object> _factory;
            
            public SerializationCallbacksFactory()
            {
                _factory = CreateTypedCallbacks<object, Action<object, StreamingContext>>;
            }

            public SerializationCallbacks<Action<object, StreamingContext>> GetReferenceTypeCallbacks(Type type) => (
                SerializationCallbacks<Action<object, StreamingContext>>)_cache.GetOrAdd(type, _factory);

            public SerializationCallbacks<TDelegate> GetValueTypeCallbacks<TOwner, TDelegate>(Type type) => (
                SerializationCallbacks<TDelegate>) _cache.GetOrAdd(type, t => (object) CreateTypedCallbacks<TOwner, TDelegate>(type));

            private SerializationCallbacks<TDelegate> CreateTypedCallbacks<TOwner, TDelegate>(Type type)
            {
                var typeInfo = type.GetTypeInfo();
                var onDeserializing = default(TDelegate);
                var onDeserialized = default(TDelegate);
                var onSerializing = default(TDelegate);
                var onSerialized = default(TDelegate);
                foreach (var method in typeInfo.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var parameterInfos = method.GetParameters();
                    if (parameterInfos.Length != 1) continue;
                    if (parameterInfos[0].ParameterType != typeof(StreamingContext)) continue;

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

        public enum SerializerTypeToken : byte
        {
            None = 0,
            Exception = 1,
            Other = 2
        }
    }
}
