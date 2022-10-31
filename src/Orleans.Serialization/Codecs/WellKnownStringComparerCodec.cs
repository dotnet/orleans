using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for well-known <see cref="StringComparer"/> types.
    /// </summary>
    [Alias("StringComparer")]
    public sealed class WellKnownStringComparerCodec : IGeneralizedCodec
    {
        private static readonly Type CodecType = typeof(WellKnownStringComparerCodec);
        private readonly StringComparer _ordinalComparer;
        private readonly StringComparer _ordinalIgnoreCaseComparer;
        private readonly EqualityComparer<string> _defaultEqualityComparer;
        private readonly Type _ordinalType;
        private readonly Type _ordinalIgnoreCaseType;
        private readonly Type _defaultEqualityType;
#if !NET6_0_OR_GREATER
        private readonly StreamingContext _streamingContext;
        private readonly FormatterConverter _formatterConverter;
#endif

        /// <summary>
        /// Initializes a new instance of the <see cref="WellKnownStringComparerCodec"/> class.
        /// </summary>
        public WellKnownStringComparerCodec()
        {
            _ordinalComparer = StringComparer.Ordinal;
            _ordinalIgnoreCaseComparer = StringComparer.OrdinalIgnoreCase;
            _defaultEqualityComparer = EqualityComparer<string>.Default;

            _ordinalType = _ordinalComparer.GetType();
            _ordinalIgnoreCaseType = _ordinalIgnoreCaseComparer.GetType();
            _defaultEqualityType = _defaultEqualityComparer.GetType();
#if !NET6_0_OR_GREATER
            _streamingContext = new StreamingContext(StreamingContextStates.All);
            _formatterConverter = new FormatterConverter();
#endif
        }

        /// <inheritdoc />
        public bool IsSupportedType(Type type) =>
            type == CodecType
            || type == _ordinalType
            || type == _ordinalIgnoreCaseType
            || type == _defaultEqualityType
            || !type.IsAbstract && typeof(IEqualityComparer<string>).IsAssignableFrom(type) && type.Assembly.Equals(typeof(IEqualityComparer<string>).Assembly);

        /// <inheritdoc />
        public object ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            field.EnsureWireTypeTagDelimited();
            ReferenceCodec.MarkValueField(reader.Session);
            uint type = default;
            CompareOptions options = default;
            int lcid = default;
            uint fieldId = 0;
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
                        type = UInt32Codec.ReadValue(ref reader, header);
                        break;
                    case 1:
                        options = (CompareOptions)UInt32Codec.ReadValue(ref reader, header);
                        break;
                    case 2:
                        lcid = Int32Codec.ReadValue(ref reader, header);
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            if (type == 0)
            {
                return null;
            }
            else if (type == 1)
            {
                if (options.HasFlag(CompareOptions.IgnoreCase))
                {
                    return StringComparer.OrdinalIgnoreCase;
                }
                else
                {
                    return StringComparer.Ordinal;
                }
            }
            else if (type == 2)
            {
                if (lcid == CultureInfo.InvariantCulture.LCID)
                {
                    if (options == CompareOptions.None)
                    {
                        return StringComparer.InvariantCulture;
                    }
                    else if (options == CompareOptions.IgnoreCase)
                    {
                        return StringComparer.InvariantCultureIgnoreCase;
                    }

                    // Otherwise, perhaps some other options were specified, in which case we fall-through to create a new comparer.
                }

                var cultureInfo = CultureInfo.GetCultureInfo(lcid);
                var result = StringComparer.Create(cultureInfo, options);
                return result;
            }

            ThrowNotSupported(field, type);
            return null;
        }

        /// <inheritdoc />
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value) where TBufferWriter : IBufferWriter<byte>
        {
            uint type;
            CompareOptions compareOptions = default;
            CompareInfo compareInfo = default;
            if (value is null)
            {
                type = 0;
            }
            else
            {
#if NET6_0_OR_GREATER
                var comparer = (IEqualityComparer<string>)value;
                if (StringComparer.IsWellKnownOrdinalComparer(comparer, out var ignoreCase))
                {
                    // Ordinal. This also handles EqualityComparer<string>.Default.
                    type = 1;
                    if (ignoreCase)
                    {
                        compareOptions = CompareOptions.IgnoreCase;
                    }
                }
                else if (StringComparer.IsWellKnownCultureAwareComparer(comparer, out compareInfo, out compareOptions))
                {
                    type = 2;
                }
                else
                {
                    ThrowNotSupported(value.GetType());
                    return;
                }
#else
                var isOrdinal = _ordinalComparer.Equals(value) || _defaultEqualityComparer.Equals(value);
                var isOrdinalIgnoreCase = _ordinalIgnoreCaseComparer.Equals(value);
                if (isOrdinal)
                {
                    type = 1;
                }
                else if (isOrdinalIgnoreCase)
                {
                    type = 1;
                    compareOptions = CompareOptions.IgnoreCase;
                }
                else if (TryGetWellKnownCultureAwareComparerInfo(value, out compareInfo, out compareOptions, out var ignoreCase))
                {
                    type = 2;
                    if (ignoreCase)
                    {
                        compareOptions |= CompareOptions.IgnoreCase;
                    }
                }
                else
                {
                    ThrowNotSupported(value.GetType());
                    return;
                }
#endif
            }

            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(WellKnownStringComparerCodec), WireType.TagDelimited);

            UInt32Codec.WriteField(ref writer, 0, UInt32Codec.CodecFieldType, type);
            UInt32Codec.WriteField(ref writer, 1, UInt32Codec.CodecFieldType, (uint)compareOptions);

            if (compareInfo is not null)
            {
                Int32Codec.WriteField(ref writer, 1, typeof(int), compareInfo.LCID);
            }

            writer.WriteEndObject();
        }

#if !NET6_0_OR_GREATER
        private bool TryGetWellKnownCultureAwareComparerInfo(object value, out CompareInfo compareInfo, out CompareOptions compareOptions, out bool ignoreCase)
        {
            compareInfo = default;
            compareOptions = default;
            ignoreCase = default;
            if (value is ISerializable serializable)
            {
                var info = new SerializationInfo(value.GetType(), _formatterConverter);
                serializable.GetObjectData(info, _streamingContext);
                foreach (var entry in info)
                {
                    switch (entry.Name)
                    {
                        case "_compareInfo":
                            compareInfo = entry.Value as CompareInfo;
                            break;
                        case "_options":
                            compareOptions = (CompareOptions)entry.Value;
                            break;
                        case "_ignoreCase":
                            ignoreCase = (bool)entry.Value;
                            break;
                    }
                }

                return compareInfo is not null;
            }

            return false;
        }
#endif

        [DoesNotReturn]
        private static void ThrowNotSupported(Field field, uint value) => throw new NotSupportedException($"Values of type {field.FieldType} are not supported. Value: {value}");

        [DoesNotReturn]
        private static void ThrowNotSupported(Type type) => throw new NotSupportedException($"Values of type {type} are not supported");
    }

    /// <summary>
    /// Serializer for <see cref="EqualityComparer{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterCopier]
    [RegisterSerializer]
    public class EqualityComparerBaseCodec<T> : IBaseCodec<EqualityComparer<T>>, IBaseCopier<EqualityComparer<T>>
    {
        /// <inheritdoc />
        public void DeepCopy(EqualityComparer<T> input, EqualityComparer<T> output, CopyContext context) { }

        /// <inheritdoc />
        public void Deserialize<TInput>(ref Reader<TInput> reader, EqualityComparer<T> value)
        {
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndBaseOrEndObject)
                {
                    break;
                }

                reader.ConsumeUnknownField(header);
            }
        }

        /// <inheritdoc />
        public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, EqualityComparer<T> value) where TBufferWriter : IBufferWriter<byte>
        {
        }
    }

    /// <summary>
    /// Serializer for <see cref="Comparer{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterCopier]
    [RegisterSerializer]
    public class ComparerBaseCodec<T> : IBaseCodec<Comparer<T>>, IBaseCopier<Comparer<T>>
    {
        /// <inheritdoc />
        public void DeepCopy(Comparer<T> input, Comparer<T> output, CopyContext context) { }

        /// <inheritdoc />
        public void Deserialize<TInput>(ref Reader<TInput> reader, Comparer<T> value)
        {
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndBaseOrEndObject)
                {
                    break;
                }

                reader.ConsumeUnknownField(header);
            }
        }

        /// <inheritdoc />
        public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, Comparer<T> value) where TBufferWriter : IBufferWriter<byte>
        {
        }
    }
}