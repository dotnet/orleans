using Microsoft.Extensions.Logging;

namespace Orleans.Serialization
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq.Expressions;
    using System.Reflection;
    using Bond;
    using Runtime;
    using BondBinaryReader = Bond.Protocols.SimpleBinaryReader<Orleans.Serialization.InputStream>;
    using BondBinaryWriter = Bond.Protocols.SimpleBinaryWriter<Orleans.Serialization.OutputStream>;
    using BondTypeDeserializer = Bond.Deserializer<Bond.Protocols.SimpleBinaryReader<Orleans.Serialization.InputStream>>;
    using BondTypeSerializer = Bond.Serializer<Bond.Protocols.SimpleBinaryWriter<Orleans.Serialization.OutputStream>>;

    /// <summary>
    /// An implementation of IExternalSerializer for usage with Bond types.
    /// </summary>
    public class BondSerializer : IExternalSerializer
    {
        private static ConcurrentDictionary<RuntimeTypeHandle, ClonerInfo> ClonerInfoDictionary;
        private static ConcurrentDictionary<RuntimeTypeHandle, BondTypeSerializer> SerializerDictionary;
        private static ConcurrentDictionary<RuntimeTypeHandle, BondTypeDeserializer> DeserializerDictionary;

        private ILogger logger;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="logger"></param>
        public BondSerializer(ILogger<BondSerializer> logger)
        {
            ClonerInfoDictionary = new ConcurrentDictionary<RuntimeTypeHandle, ClonerInfo>();
            SerializerDictionary = new ConcurrentDictionary<RuntimeTypeHandle, BondTypeSerializer>();
            DeserializerDictionary = new ConcurrentDictionary<RuntimeTypeHandle, BondTypeDeserializer>();
            this.logger = logger;
        }

        /// <summary>
        /// Determines whether this serializer has the ability to serialize a particular type.
        /// </summary>
        /// <param name="itemType">The type of the item to be serialized</param>
        /// <returns>A value indicating whether the type can be serialized</returns>
        public bool IsSupportedType(Type itemType)
        {
            if (ClonerInfoDictionary.ContainsKey(itemType.TypeHandle))
            {
                return true;
            }

            var typeInfo = itemType.GetTypeInfo();
            if (typeInfo.IsGenericType && itemType.IsConstructedGenericType == false)
            {
                return false;
            }

            if (typeInfo.GetCustomAttribute<SchemaAttribute>() == null)
            {
                return false;
            }

            Register(itemType);
            return true;
        }

        /// <inheritdoc />
        public object DeepCopy(object source, ICopyContext context)
        {
            if (source == null)
            {
                return null;
            }

            var clonerInfo = GetClonerInfo(source.GetType().TypeHandle);
            if (clonerInfo == null)
            {
                LogWarning(1, "no copier found for type {0}", source.GetType());
                throw new ArgumentOutOfRangeException("original", "no copier provided for the selected type");
            }

            return clonerInfo.Invoke(source);
        }

        /// <inheritdoc />
        public object Deserialize(Type expectedType, IDeserializationContext context)
        {
            if (expectedType == null)
            {
                throw new ArgumentNullException(nameof(expectedType));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var typeHandle = expectedType.TypeHandle;
            var deserializer = GetDeserializer(typeHandle);
            if (deserializer == null)
            {
                LogWarning(3, "no deserializer found for type {0}", expectedType.FullName);
                throw new ArgumentOutOfRangeException("no deserializer provided for the selected type", "expectedType");
            }

            var inputStream = InputStream.Create(context.StreamReader);
            var bondReader = new BondBinaryReader(inputStream);
            return deserializer.Deserialize(bondReader);
        }

        /// <inheritdoc />
        public void Serialize(object item, ISerializationContext context, Type expectedType)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var writer = context.StreamWriter;
            if (item == null)
            {
                writer.WriteNull();
                return;
            }

            var typeHandle = item.GetType().TypeHandle;
            var serializer = GetSerializer(typeHandle);
            if (serializer == null)
            {
                LogWarning(2, "no serializer found for type {0}", item.GetType());
                throw new ArgumentOutOfRangeException("no serializer provided for the selected type", "untypedInput");
            }

            var outputStream = OutputStream.Create(writer);
            var bondWriter = new BondBinaryWriter(outputStream);
            serializer.Serialize(item, bondWriter);
        }

        private static object DeepCopy<T>(T source, object cloner)
        {       
            return ((Cloner<T>)cloner).Clone<T>(source);
        }

        private static ClonerInfo GetClonerInfo(RuntimeTypeHandle handle)
        {
            return Get(ClonerInfoDictionary, handle);
        }

        private static BondTypeSerializer GetSerializer(RuntimeTypeHandle handle)
        {
            return Get(SerializerDictionary, handle);
        }

        private static BondTypeDeserializer GetDeserializer(RuntimeTypeHandle handle)
        {
            return Get(DeserializerDictionary, handle);
        }

        private static TValue Get<TValue>(IDictionary<RuntimeTypeHandle, TValue> dictionary, RuntimeTypeHandle key)
        {
            TValue value;
            dictionary.TryGetValue(key, out value);
            return value;
        }

        private void LogWarning(int code, string format, params object[] parameters)
        {
            if(logger.IsEnabled(LogLevel.Warning))
                logger.Warn(code, format, parameters);
        }

        private void Register(Type type)
        {
            var clonerType = typeof(Cloner<>);
            var realType = clonerType.MakeGenericType(type);
            var clonerInstance = Activator.CreateInstance(realType);
            var serializer = new BondTypeSerializer(type);
            var deserializer = new BondTypeDeserializer(type);
            var sourceParameter = Expression.Parameter(typeof(object));
            var instanceParameter = Expression.Parameter(typeof(object));
            var method = typeof(BondSerializer).GetMethod("DeepCopy", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(type);
            var lambda = Expression.Lambda(
                   typeof(Func<object, object, object>),
                   Expression.Call(
                       method,
                       Expression.Convert(sourceParameter, type),
                       instanceParameter),
                   sourceParameter,
                   instanceParameter);
            var copierDelegate = (Func<object, object, object>)lambda.Compile();
            ClonerInfoDictionary.TryAdd(type.TypeHandle, new ClonerInfo(clonerInstance, copierDelegate));
            SerializerDictionary.TryAdd(type.TypeHandle, serializer);
            DeserializerDictionary.TryAdd(type.TypeHandle, deserializer);
        }

        private class ClonerInfo
        {
            private readonly object instance;

            private readonly Func<object, object, object> func;

            internal ClonerInfo(object clonerInstance, Func<object, object, object> func)
            {
                this.instance = clonerInstance;
                this.func = func;
            }

            public object Invoke(object source)
            {
                return this.func(source, this.instance);
            }
        }
    }
}
