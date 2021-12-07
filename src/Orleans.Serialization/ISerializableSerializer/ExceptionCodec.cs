using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TypeSystem;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.Serialization;

namespace Orleans.Serialization
{
    [RegisterSerializer]
    [RegisterCopier]
    [WellKnownAlias("Exception")]
    public class ExceptionCodec : IFieldCodec<Exception>, IBaseCodec<Exception>, IGeneralizedCodec, IGeneralizedBaseCodec, IBaseCopier<Exception>
    {
        private readonly StreamingContext _streamingContext;
        private readonly FormatterConverter _formatterConverter;
        private readonly Func<Type, Action<object, SerializationInfo, StreamingContext>> _createConstructorDelegate;
        private readonly Action<object, SerializationInfo, StreamingContext> _baseExceptionConstructor;
        private readonly IServiceProvider _serviceProvider;
        private readonly TypeConverter _typeConverter;
        private readonly IFieldCodec<Dictionary<object, object>> _dictionaryCodec;
        private readonly IDeepCopier<Dictionary<object, object>> _dictionaryCopier;
        private readonly IDeepCopier<Exception> _exceptionCopier;

        public ExceptionCodec(
            IServiceProvider serviceProvider,
            TypeConverter typeConverter,
            IFieldCodec<Dictionary<object, object>> dictionaryCodec,
            IDeepCopier<Dictionary<object, object>> dictionaryCopier,
            IDeepCopier<Exception> exceptionCopier)
        {
            _streamingContext = new StreamingContext(StreamingContextStates.All);
            _formatterConverter = new FormatterConverter();
            var constructorFactory = new SerializationConstructorFactory();
            _createConstructorDelegate = constructorFactory.GetSerializationConstructorDelegate;
            _baseExceptionConstructor = _createConstructorDelegate(typeof(Exception));
            _serviceProvider = serviceProvider;
            _typeConverter = typeConverter;
            _dictionaryCodec = dictionaryCodec;
            _dictionaryCopier = dictionaryCopier;
            _exceptionCopier = exceptionCopier;
        }

        public void Deserialize<TInput>(ref Reader<TInput> reader, Exception value)
        {
            uint fieldId = 0;
            string message = null;
            string stackTrace = null;
            Exception innerException = null;
            Dictionary<object, object> data = null;
            int hResult = 0;
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndBaseOrEndObject)
                {
                    break;
                }

                fieldId += header.FieldIdDelta;
                switch (fieldId)
                {
                    case 0:
                        message = StringCodec.ReadValue(ref reader, header);
                        break;
                    case 1:
                        stackTrace = StringCodec.ReadValue(ref reader, header);
                        break;
                    case 2:
                        innerException = ReadValue(ref reader, header);
                        break;
                    case 3:
                        hResult = Int32Codec.ReadValue(ref reader, header);
                        break;
                    case 4:
                        data = _dictionaryCodec.ReadValue(ref reader, header);
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            SetBaseProperties(value, message, stackTrace, innerException, hResult, data);
        }

        public SerializationInfo GetObjectData(Exception value)
        {
            var info = new SerializationInfo(value.GetType(), _formatterConverter);
            value.GetObjectData(info, _streamingContext);
            return info;
        }

        public void SetBaseProperties(Exception value, string message, string stackTrace, Exception innerException, int hResult, Dictionary<object, object> data)
        {
            var info = new SerializationInfo(typeof(Exception), _formatterConverter);
            info.AddValue("Message", message, typeof(string));
            info.AddValue("StackTraceString", null, typeof(string));
            info.AddValue("InnerException", innerException, typeof(Exception));
            info.AddValue("ClassName", value.GetType().ToString(), typeof(string));
            info.AddValue("Data", null, typeof(IDictionary));
            info.AddValue("HelpURL", null, typeof(string));
            info.AddValue("RemoteStackTraceString", stackTrace, typeof(string));
            info.AddValue("RemoteStackIndex", 0, typeof(int));
            info.AddValue("ExceptionMethod", null, typeof(string));
            info.AddValue("HResult", hResult);
            info.AddValue("Source", null, typeof(string));
            info.AddValue("WatsonBuckets", null, typeof(byte[]));

            _baseExceptionConstructor(value, info, _streamingContext);
            if (data is { })
            {
                foreach (var pair in data)
                {
                    value.Data[pair.Key] = pair.Value;
                }
            }
        }

        public Dictionary<object, object> GetDataProperty(Exception exception)
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

        public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, Exception value) where TBufferWriter : IBufferWriter<byte>
        {
            StringCodec.WriteField(ref writer, 0, typeof(string), value.Message);
            StringCodec.WriteField(ref writer, 1, typeof(string), value.StackTrace);
            WriteField(ref writer, 1, typeof(Exception), value.InnerException);
            Int32Codec.WriteField(ref writer, 1, typeof(int), value.HResult);
            if (GetDataProperty(value) is { } dataDictionary)
            {
                _dictionaryCodec.WriteField(ref writer, 1, typeof(Dictionary<object, object>), dataDictionary);
            }
        }

        public void SerializeException<TBufferWriter>(ref Writer<TBufferWriter> writer, Exception value) where TBufferWriter : IBufferWriter<byte>
        {
            StringCodec.WriteField(ref writer, 0, typeof(string), _typeConverter.Format(value.GetType()));
            StringCodec.WriteField(ref writer, 1, typeof(string), value.Message);
            StringCodec.WriteField(ref writer, 1, typeof(string), value.StackTrace);
            WriteField(ref writer, 1, typeof(Exception), value.InnerException);
            Int32Codec.WriteField(ref writer, 1, typeof(int), value.HResult);
            if (GetDataProperty(value) is { } dataDictionary)
            {
                _dictionaryCodec.WriteField(ref writer, 1, typeof(Dictionary<object, object>), dataDictionary);
            }
        }

        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Exception value) where TBufferWriter : IBufferWriter<byte>
        {
            if (value is null)
            {
                ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta, expectedType);
                return;
            }

            if (value.GetType() == typeof(Exception))
            {
                // Exceptions are never written as references. This ensures that reference cycles in exceptions are not possible and is a security precaution. 
                ReferenceCodec.MarkValueField(writer.Session);
                writer.WriteStartObject(fieldIdDelta, expectedType, typeof(ExceptionCodec));
                SerializeException(ref writer, value);
                writer.WriteEndObject();
            }
            else
            {
                OrleansGeneratedCodeHelper.SerializeUnexpectedType(ref writer, fieldIdDelta, expectedType, value);
            }
        }

        public bool IsSupportedType(Type type)
        {
            if (type == typeof(ExceptionCodec))
            {
                return true;
            }

            if (type == typeof(AggregateException))
            {
                return false;
            }

            if (typeof(Exception).IsAssignableFrom(type) && type.Namespace is { } ns && (ns.StartsWith("System", StringComparison.Ordinal) || ns.StartsWith("Microsoft", StringComparison.Ordinal)))
            {
                return true;
            }

            return false;
        }

        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value) where TBufferWriter : IBufferWriter<byte>
        {
            if (value is null)
            {
                ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta, expectedType);
                return;
            }

            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteStartObject(fieldIdDelta, expectedType, typeof(ExceptionCodec));
            SerializeException(ref writer, (Exception)value);
            writer.WriteEndObject();
       }

        public Exception ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            // In order to handle null values.
            if (field.WireType == WireType.Reference)
            {
                // Discard the result of consuming the reference and always return null.
                // We do not allow exceptions to participate in reference cycles because cycles involving InnerException are not allowed by .NET
                // Exceptions must never form cyclic graphs via their well-known properties/fields (eg, InnerException).
                var _ = ReferenceCodec.ReadReference<Exception, TInput>(ref reader, field);
                return null;
            }

            Type valueType = field.FieldType;
            if (valueType is null || valueType == typeof(Exception))
            {
                return DeserializeException(ref reader, field);
            }

            return OrleansGeneratedCodeHelper.DeserializeUnexpectedType<TInput, Exception>(ref reader, field);
        }

        object IFieldCodec<object>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<Exception, TInput>(ref reader, field);
            }

            return DeserializeException(ref reader, field);
        }
        
        public Exception DeserializeException<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);

            uint fieldId = 0;
            string typeName = null;
            string message = null;
            string stackTrace = null;
            Exception innerException = null;
            Dictionary<object, object> data = null;
            int hResult = 0;
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndBaseOrEndObject)
                {
                    break;
                }

                fieldId += header.FieldIdDelta;
                switch (fieldId)
                {
                    case 0:
                        typeName = StringCodec.ReadValue(ref reader, header);
                        break;
                    case 1:
                        message = StringCodec.ReadValue(ref reader, header);
                        break;
                    case 2:
                        stackTrace = StringCodec.ReadValue(ref reader, header);
                        break;
                    case 3:
                        innerException = ReadValue(ref reader, header);
                        break;
                    case 4:
                        hResult = Int32Codec.ReadValue(ref reader, header);
                        break;
                    case 5:
                        data = _dictionaryCodec.ReadValue(ref reader, header);
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            Exception result;
            if (!_typeConverter.TryParse(typeName, out var type))
            {
                result = new UnavailableExceptionFallbackException
                {
                    ExceptionType = typeName
                };
            }
            else if (typeof(Exception).IsAssignableFrom(type))
            {
                result = (Exception)Activator.CreateInstance(type);
            }
            else
            {
                throw new NotSupportedException("Type {type} is not supported");
            }

            SetBaseProperties(result, message, stackTrace, innerException, hResult, data);
            return result;
        }

        public void DeepCopy(Exception input, Exception output, CopyContext context)
        {
            var info = GetObjectData(input);
            SetBaseProperties(
                output,
                // Get the message from object data in case the property is overridden as it is with AggregateException
                info.GetString("Message"),
                input.StackTrace,
                _exceptionCopier.DeepCopy(input.InnerException, context),
                input.HResult,
                _dictionaryCopier.DeepCopy(GetDataProperty(input), context));
        }

        public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, object value) where TBufferWriter : IBufferWriter<byte> => Serialize(ref writer, (Exception)value);
        public void Deserialize<TInput>(ref Reader<TInput> reader, object value) => Deserialize(ref reader, (Exception)value);
    }

    [RegisterSerializer]
    public class AggregateExceptionCodec : GeneralizedReferenceTypeSurrogateCodec<AggregateException, AggregateExceptionSurrogate>
    {
        private readonly ExceptionCodec _baseCodec;
        public AggregateExceptionCodec(ExceptionCodec baseCodec, IValueSerializer<AggregateExceptionSurrogate> surrogateSerializer) : base(surrogateSerializer)
        {
            _baseCodec = baseCodec;
        }

        public override AggregateException ConvertFromSurrogate(ref AggregateExceptionSurrogate surrogate)
        {
            var result = new AggregateException(surrogate.InnerExceptions);
            var innerException = surrogate.InnerExceptions is { Count: > 0 } innerExceptions ? innerExceptions[0] : null;
            _baseCodec.SetBaseProperties(result, surrogate.Message, surrogate.StackTrace, innerException, surrogate.HResult, surrogate.Data);
            return result;
        }

        public override void ConvertToSurrogate(AggregateException value, ref AggregateExceptionSurrogate surrogate)
        {
            var info = _baseCodec.GetObjectData(value);
            surrogate.Message = info.GetString("Message");
            surrogate.StackTrace = value.StackTrace;
            surrogate.HResult = value.HResult;
            var data = info.GetValue("Data", typeof(IDictionary));
            if (data is { })
            {
                surrogate.Data = _baseCodec.GetDataProperty(value);
            }

            if (value.InnerExceptions is { } iexs)
            {
                surrogate.InnerExceptions = value.InnerExceptions;
            }
        }
    }

    [GenerateSerializer]
    public struct AggregateExceptionSurrogate
    {
        [Id(0)]
        public string Message { get; set; }

        [Id(1)]
        public string StackTrace { get; set; }

        [Id(2)]
        public Dictionary<object, object> Data { get; set; }

        [Id(3)]
        public int HResult { get; set; }

        [Id(4)]
        public ReadOnlyCollection<Exception> InnerExceptions { get; set; }
    }
}