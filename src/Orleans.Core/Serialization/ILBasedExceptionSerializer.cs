using System.Runtime.Serialization;
using Orleans.Utilities;

namespace Orleans.Serialization
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Reflection;

    /// <summary>
    /// Methods for serializing instances of <see cref="Exception"/> and its subclasses.
    /// </summary>
    internal class ILBasedExceptionSerializer
    {
        private static readonly Type ExceptionType = typeof(Exception);

        private HashSet<string> SkippedProperties { get; } = new HashSet<string>
            {
                "Message",
                "StackTraceString",
                "InnerException",
                "ClassName",
                "Data",
                "HelpURL",
                "RemoteStackTraceString",
                "RemoteStackIndex",
                "ExceptionMethod",
                "HResult",
                "Source",
                "WatsonBuckets",
            };

        /// <summary>
        /// The collection of serializers.
        /// </summary>
        private readonly CachedReadConcurrentDictionary<Type, SerializerMethods> serializers =
            new CachedReadConcurrentDictionary<Type, SerializerMethods>();

        /// <summary>
        /// The serializer used as a fallback when the concrete exception type is unavailable.
        /// </summary>
        /// <remarks>
        /// This serializer operates on <see cref="ExceptionBaseProperties"/> instances, however it 
        /// includes only fields from <see cref="Exception"/> and no sub-class fields.
        /// </remarks>
        private readonly SerializerMethods propsSerializer;
        
        /// <summary>
        /// The serializer generator.
        /// </summary>
        private readonly ILSerializerGenerator generator;

        private readonly TypeSerializer typeSerializer;
        private readonly StreamingContext _streamingContext;
        private readonly FormatterConverter _formatterConverter;
        private readonly SerializationConstructorFactory _constructorFactory;
        private readonly Func<Type, Action<object, SerializationInfo, StreamingContext>> _createConstructorDelegate;
        private readonly Action<object, SerializationInfo, StreamingContext> _baseExceptionConstructor;

        public ILBasedExceptionSerializer(ILSerializerGenerator generator, TypeSerializer typeSerializer)
        {
            _streamingContext = new StreamingContext(StreamingContextStates.All);
            _formatterConverter = new FormatterConverter();
            _constructorFactory = new SerializationConstructorFactory();
            _createConstructorDelegate = _constructorFactory.GetSerializationConstructorDelegate;
            _baseExceptionConstructor = _createConstructorDelegate(typeof(Exception));

            this.generator = generator;
            this.typeSerializer = typeSerializer;

            // When serializing the fallback type, only the fields declared on Exception are included.
            // Other fields are manually serialized.
            this.propsSerializer = this.generator.GenerateSerializer(typeof(ExceptionBaseProperties));

            // Ensure that the fallback serializer only ever has its base exception fields serialized.
            this.serializers[typeof(ExceptionBaseProperties)] = this.propsSerializer;
        }

        public SerializationInfo GetObjectData(Exception value)
        {
            var info = new SerializationInfo(value.GetType(), _formatterConverter);
            value.GetObjectData(info, _streamingContext);
            return info;
        }

        private static Dictionary<object, object> GetDataProperty(Exception exception)
        {
            if (exception.Data is null or { Count: 0 })
            {
                return null;
            }

            var tmp = new Dictionary<object, object>(exception.Data.Count);
            var enumerator = exception.Data.GetEnumerator();
            while (enumerator.MoveNext())
            {
                var entry = enumerator.Entry;
                tmp[entry.Key] = entry.Value;
            }

            return tmp;
        }


        public void Serialize(object item, ISerializationContext outerContext, Type expectedType)
        {
            var outerWriter = outerContext.StreamWriter;

            var actualType = item.GetType();

            // To support loss-free serialization where possible, instances of the fallback exception type are serialized in a
            // semi-manual fashion.
            var fallbackException = item as RemoteNonDeserializableException;
            if (fallbackException != null)
            {
                // Write the type name directly.
                var key = new TypeSerializer.TypeKey(fallbackException.OriginalTypeName);
                TypeSerializer.WriteTypeKey(key, outerWriter);
            }
            else
            {
                // Write the concrete type directly.
                this.typeSerializer.WriteNamedType(actualType, outerWriter);
            }

            // Create a nested context which will be written to the outer context at an int-length offset from the current position.
            // This is because the inner context will be copied with a length prefix to the outer context.
            var innerWriter = new BinaryTokenStreamWriter();
            var innerContext = outerContext.CreateNestedContext(position: outerContext.CurrentOffset + sizeof(int), writer: innerWriter);

            var typedValue = (Exception)item;
            var props = new ExceptionBaseProperties
            {
                Message = typedValue.Message,
                StackTrace = typedValue.StackTrace,
                InnerException = typedValue.InnerException,
                HResult = typedValue.HResult,
                Data = GetDataProperty(typedValue),
            };

            var data = GetObjectData(typedValue);
            var serializationManager = innerContext.GetSerializationManager();
            if (fallbackException is { })
            {
                props.AdditionalData = fallbackException.AdditionalData;
            }
            else
            {
                // Build up the additional data by enumerating the populated serialization info.
                props.AdditionalData = new();
                foreach (var pair in data)
                {
                    if (SkippedProperties.Contains(pair.Name)) continue;

                    var bytes = serializationManager.SerializeToByteArray(pair.Value);
                    var type = pair.Value?.GetType() ?? pair.ObjectType ?? typeof(object);
                    var typeName = typeSerializer.GetNameFromType(type);
                    props.AdditionalData[pair.Name] = (typeName, bytes);
                }
            }

            innerContext.SerializeInner(props, typeof(ExceptionBaseProperties));

            // Write the serialized exception to the output stream.
            outerContext.StreamWriter.Write(innerWriter.CurrentOffset);
            outerContext.StreamWriter.Write(innerWriter.ToBytes());
        }

        public object Deserialize(Type expectedType, IDeserializationContext outerContext)
        {
            var outerReader = outerContext.StreamReader;

            var typeKey = TypeSerializer.ReadTypeKey(outerReader);
            
            // Read the length of the serialized payload.
            var length = outerReader.ReadInt();

            // The nested data was serialized beginning at the current offset (after the length property).
            // Record the current offset for use when creating the nested deserialization context.
            var position = outerContext.CurrentPosition;

            // Read the nested payload.
            var innerBytes = outerReader.ReadBytes(length);
            
            var innerContext = outerContext.CreateNestedContext(position: position, reader: new BinaryTokenStreamReader(innerBytes));

            // If the concrete type is available and the exception is valid for reconstruction,
            // reconstruct the original exception type.
            var actualType = this.typeSerializer.GetTypeFromTypeKey(typeKey, throwOnError: false);
            var props = (ExceptionBaseProperties)innerContext.DeserializeInner(typeof(ExceptionBaseProperties));

            var resultType = actualType ?? typeof(RemoteNonDeserializableException);
            var result = (Exception)FormatterServices.GetUninitializedObject(resultType);

            var info = new SerializationInfo(resultType, _formatterConverter);
            info.AddValue("Message", props.Message, typeof(string));
            info.AddValue("StackTraceString", null, typeof(string));
            info.AddValue("InnerException", props.InnerException, typeof(Exception));
            info.AddValue("ClassName", result.GetType().ToString(), typeof(string));
            info.AddValue("Data", null, typeof(IDictionary));
            info.AddValue("HelpURL", null, typeof(string));
            info.AddValue("RemoteStackTraceString", props.StackTrace, typeof(string));
            info.AddValue("RemoteStackIndex", 0, typeof(int));
            info.AddValue("ExceptionMethod", null, typeof(string));
            info.AddValue("HResult", props.HResult);
            info.AddValue("Source", null, typeof(string));
            info.AddValue("WatsonBuckets", null, typeof(byte[]));

            var serializationManager = innerContext.GetSerializationManager();
            if (props.AdditionalData is not null)
            {
                foreach (var pair in props.AdditionalData)
                {
                    if (SkippedProperties.Contains(pair.Key)) continue;

                    try
                    {
                        var pairTypeKey = new TypeSerializer.TypeKey(pair.Value.Item1);
                        var valueType = this.typeSerializer.GetTypeFromTypeKey(pairTypeKey, throwOnError: false);
                        if (valueType is not null)
                        {
                            var value = serializationManager.DeserializeFromByteArray(pair.Value.Item2, valueType);
                            info.AddValue(pair.Key, value, valueType);
                        }
                    }
                    catch
                    {
                        // Ignore and skip.
                    }
                }
            }

            if (resultType == typeof(RemoteNonDeserializableException))
            {
                info.AddValue(nameof(RemoteNonDeserializableException.OriginalTypeName), typeKey.GetTypeName());
                info.AddValue(nameof(RemoteNonDeserializableException.AdditionalData), props.AdditionalData);
            }

            // Find the most suitable constructor
            Action<object, SerializationInfo, StreamingContext> ctor;
            if (SerializationConstructorFactory.HasSerializationConstructor(resultType))
            {
                ctor = _constructorFactory.GetSerializationConstructorDelegate(resultType);
            }
            else
            {
                ctor = _baseExceptionConstructor;
            }

            ctor(result, info, _streamingContext);

            if (props.Data is { } data && result.Data is not null)
            {
                foreach (var pair in data)
                {
                    result.Data[pair.Key] = pair.Value;
                }
            }

            return result;
        }

        /// <summary>
        /// Returns a copy of the provided instance.
        /// </summary>
        /// <param name="original">The object to copy.</param>
        /// <param name="context">The copy context.</param>
        /// <returns>A copy of the provided instance.</returns>
        public object DeepCopy(object original, ICopyContext context)
        {
            return original;
        }
    }

    [Serializable]
    internal sealed class ExceptionBaseProperties
    {
        public string Message { get; set; }
        public string StackTrace { get; set; }
        public Exception InnerException { get; set; }
        public int HResult { get; set; }
        public Dictionary<object, object> Data { get; set; }
        public Dictionary<string, (string, byte[])> AdditionalData { get; set; }
    }
}