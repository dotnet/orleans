using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;

#nullable disable
namespace Orleans.Serialization.Codecs;

internal static class InterfaceCollectionCodecHelpers
{
    private const int MaxCapacityHint = 16 * 1024;

    public static bool TryWriteRuntimeCodec<TBufferWriter, TField>(
        ref Writer<TBufferWriter> writer,
        uint fieldIdDelta,
        Type expectedType,
        TField value) where TBufferWriter : IBufferWriter<byte>
    {
        if (value is null)
        {
            ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
            return true;
        }

        if (writer.Session.CodecProvider.TryGetCodec(value.GetType()) is not { } codec)
        {
            return false;
        }

        codec.WriteField(ref writer, fieldIdDelta, expectedType, value);
        return true;
    }

    public static bool TryReadReferenceOrSpecificCodec<TField, TInput>(
        ref Reader<TInput> reader,
        Field field,
        Type fallbackType,
        Type codecType,
        out TField result)
    {
        if (field.WireType == WireType.Reference)
        {
            result = ReferenceCodec.ReadReference<TField, TInput>(ref reader, field);
            return true;
        }

        var fieldType = field.FieldType;
        if (fieldType is not null
            && fieldType != typeof(TField)
            && fieldType != fallbackType
            && fieldType != codecType
            && reader.Session.CodecProvider.TryGetCodec(fieldType) is { } specificCodec)
        {
            result = (TField)specificCodec.ReadValue(ref reader, field);
            return true;
        }

        result = default;
        return false;
    }

    public static bool TryCopyRuntime<TField>(
        IDeepCopierProvider copierProvider,
        TField input,
        CopyContext context,
        out TField result) where TField : class
    {
        if (input is null)
        {
            result = null;
            return true;
        }

        if (copierProvider.TryGetDeepCopier(input.GetType()) is not { } copier)
        {
            result = null;
            return false;
        }

        result = (TField)copier.DeepCopy(input, context);
        return true;
    }

    public static bool TryGetCount<T>(IEnumerable<T> value, out int count)
    {
        switch (value)
        {
            case ICollection<T> collection:
                count = collection.Count;
                return true;
            case IReadOnlyCollection<T> collection:
                count = collection.Count;
                return true;
            default:
                count = 0;
                return false;
        }
    }

    public static int GetCapacityHint(int count) => count <= 0 ? 0 : Math.Min(count, MaxCapacityHint);

    public static int ReadCapacityHint<TInput>(ref Reader<TInput> reader, Field field)
    {
        var capacityHint = UInt32Codec.ReadValue(ref reader, field);
        return capacityHint > MaxCapacityHint ? MaxCapacityHint : (int)capacityHint;
    }

    public static bool TryWriteCapacityHint<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, int count) where TBufferWriter : IBufferWriter<byte>
    {
        var capacityHint = GetCapacityHint(count);
        if (capacityHint <= 0)
        {
            return false;
        }

        UInt32Codec.WriteField(ref writer, fieldIdDelta, (uint)capacityHint);
        return true;
    }

    public static bool TryGetInterfaceTypeForCodecType(Type type, out Type interfaceType)
    {
        if (type is null || !type.IsConstructedGenericType)
        {
            interfaceType = null;
            return false;
        }

        var genericTypeDefinition = type.GetGenericTypeDefinition();
        var arguments = type.GetGenericArguments();
        if (genericTypeDefinition == typeof(EnumerableCodec<>))
        {
            interfaceType = typeof(IEnumerable<>).MakeGenericType(arguments);
            return true;
        }

        if (genericTypeDefinition == typeof(ReadOnlyCollectionInterfaceCodec<>))
        {
            interfaceType = typeof(IReadOnlyCollection<>).MakeGenericType(arguments);
            return true;
        }

        if (genericTypeDefinition == typeof(ReadOnlyListInterfaceCodec<>))
        {
            interfaceType = typeof(IReadOnlyList<>).MakeGenericType(arguments);
            return true;
        }

        if (genericTypeDefinition == typeof(CollectionInterfaceCodec<>))
        {
            interfaceType = typeof(ICollection<>).MakeGenericType(arguments);
            return true;
        }

        if (genericTypeDefinition == typeof(ListInterfaceCodec<>))
        {
            interfaceType = typeof(IList<>).MakeGenericType(arguments);
            return true;
        }

        if (genericTypeDefinition == typeof(SetInterfaceCodec<>))
        {
            interfaceType = typeof(ISet<>).MakeGenericType(arguments);
            return true;
        }

#if NET5_0_OR_GREATER
        if (genericTypeDefinition == typeof(ReadOnlySetInterfaceCodec<>))
        {
            interfaceType = typeof(IReadOnlySet<>).MakeGenericType(arguments);
            return true;
        }
#endif

        if (genericTypeDefinition == typeof(DictionaryInterfaceCodec<,>))
        {
            interfaceType = typeof(IDictionary<,>).MakeGenericType(arguments);
            return true;
        }

        if (genericTypeDefinition == typeof(ReadOnlyDictionaryInterfaceCodec<,>))
        {
            interfaceType = typeof(IReadOnlyDictionary<,>).MakeGenericType(arguments);
            return true;
        }

        interfaceType = null;
        return false;
    }
}

internal sealed class InterfaceCollectionCodecResolver(IServiceProvider serviceProvider) : IGeneralizedCodec
{
    public bool IsSupportedType(Type type) => InterfaceCollectionCodecHelpers.TryGetInterfaceTypeForCodecType(type, out _);

    public object ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (!InterfaceCollectionCodecHelpers.TryGetInterfaceTypeForCodecType(field.FieldType, out var interfaceType))
        {
            throw new InvalidOperationException($"Type {field.FieldType} is not a supported interface collection codec type.");
        }

        return serviceProvider.GetRequiredService<ICodecProvider>().GetCodec(interfaceType).ReadValue(ref reader, field);
    }

    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value) where TBufferWriter : IBufferWriter<byte>
    {
        if (!InterfaceCollectionCodecHelpers.TryGetInterfaceTypeForCodecType(expectedType, out var interfaceType))
        {
            throw new InvalidOperationException($"Type {expectedType} is not a supported interface collection codec type.");
        }

        serviceProvider.GetRequiredService<ICodecProvider>().GetCodec(interfaceType).WriteField(ref writer, fieldIdDelta, interfaceType, value);
    }
}

internal abstract class ListInterfaceCodec<TInterface, T> : IFieldCodec<TInterface> where TInterface : class, IEnumerable<T>
{
    private static readonly Type FallbackType = typeof(List<T>);
    private static readonly Type ElementType = typeof(T);
    private readonly IFieldCodec<T> _elementCodec;

    protected ListInterfaceCodec(IFieldCodec<T> elementCodec)
    {
        _elementCodec = OrleansGeneratedCodeHelper.UnwrapService(this, elementCodec);
    }

    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TInterface value) where TBufferWriter : IBufferWriter<byte>
    {
        if (InterfaceCollectionCodecHelpers.TryWriteRuntimeCodec(ref writer, fieldIdDelta, expectedType, value))
        {
            return;
        }

        var codecType = GetType();
        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, codecType, value))
        {
            return;
        }

        writer.WriteFieldHeader(fieldIdDelta, expectedType, codecType, WireType.TagDelimited);
        if (InterfaceCollectionCodecHelpers.TryGetCount(value, out var count))
        {
            WriteElements(ref writer, value, count);
        }
        else
        {
            var snapshot = new List<T>();
            foreach (var element in value)
            {
                snapshot.Add(element);
            }

            WriteElements(ref writer, snapshot, snapshot.Count);
        }

        writer.WriteEndObject();
    }

    public TInterface ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (InterfaceCollectionCodecHelpers.TryReadReferenceOrSpecificCodec<TInterface, TInput>(ref reader, field, FallbackType, GetType(), out var result))
        {
            return result;
        }

        return (TInterface)(object)ReadFallback(ref reader, field);
    }

    private void WriteElements<TBufferWriter>(ref Writer<TBufferWriter> writer, IEnumerable<T> value, int count) where TBufferWriter : IBufferWriter<byte>
    {
        if (!InterfaceCollectionCodecHelpers.TryWriteCapacityHint(ref writer, 0, count))
        {
            return;
        }

        uint innerFieldIdDelta = 1;
        foreach (var element in value)
        {
            _elementCodec.WriteField(ref writer, innerFieldIdDelta, ElementType, element);
            innerFieldIdDelta = 0;
        }
    }

    private List<T> ReadFallback<TInput>(ref Reader<TInput> reader, Field field)
    {
        field.EnsureWireTypeTagDelimited();

        var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
        List<T> result = null;
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
                    result = new(InterfaceCollectionCodecHelpers.ReadCapacityHint(ref reader, header));
                    ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
                    break;
                case 1:
                    if (result is null)
                    {
                        result = [];
                        ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
                    }

                    result.Add(_elementCodec.ReadValue(ref reader, header));
                    break;
                default:
                    reader.ConsumeUnknownField(header);
                    break;
            }
        }

        if (result is null)
        {
            result = [];
            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
        }

        return result;
    }
}

internal abstract class SetInterfaceCodec<TInterface, T> : IFieldCodec<TInterface> where TInterface : class, IEnumerable<T>
{
    private static readonly Type FallbackType = typeof(HashSet<T>);
    private static readonly Type ElementType = typeof(T);
    private readonly IFieldCodec<T> _elementCodec;
    private readonly IFieldCodec<IEqualityComparer<T>> _comparerCodec;

    protected SetInterfaceCodec(IFieldCodec<T> elementCodec, IFieldCodec<IEqualityComparer<T>> comparerCodec)
    {
        _elementCodec = OrleansGeneratedCodeHelper.UnwrapService(this, elementCodec);
        _comparerCodec = OrleansGeneratedCodeHelper.UnwrapService(this, comparerCodec);
    }

    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TInterface value) where TBufferWriter : IBufferWriter<byte>
    {
        if (InterfaceCollectionCodecHelpers.TryWriteRuntimeCodec(ref writer, fieldIdDelta, expectedType, value))
        {
            return;
        }

        var codecType = GetType();
        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, codecType, value))
        {
            return;
        }

        writer.WriteFieldHeader(fieldIdDelta, expectedType, codecType, WireType.TagDelimited);
        if (InterfaceCollectionCodecHelpers.TryGetCount(value, out var count))
        {
            WriteElements(ref writer, value, count);
        }
        else
        {
            var snapshot = new List<T>();
            foreach (var element in value)
            {
                snapshot.Add(element);
            }

            WriteElements(ref writer, snapshot, snapshot.Count);
        }

        writer.WriteEndObject();
    }

    public TInterface ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (InterfaceCollectionCodecHelpers.TryReadReferenceOrSpecificCodec<TInterface, TInput>(ref reader, field, FallbackType, GetType(), out var result))
        {
            return result;
        }

        return (TInterface)(object)ReadFallback(ref reader, field);
    }

    private void WriteElements<TBufferWriter>(ref Writer<TBufferWriter> writer, IEnumerable<T> value, int count) where TBufferWriter : IBufferWriter<byte>
    {
        if (!InterfaceCollectionCodecHelpers.TryWriteCapacityHint(ref writer, 1, count))
        {
            return;
        }

        uint innerFieldIdDelta = 1;
        foreach (var element in value)
        {
            _elementCodec.WriteField(ref writer, innerFieldIdDelta, ElementType, element);
            innerFieldIdDelta = 0;
        }
    }

    private HashSet<T> ReadFallback<TInput>(ref Reader<TInput> reader, Field field)
    {
        field.EnsureWireTypeTagDelimited();

        var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
        HashSet<T> result = null;
        IEqualityComparer<T> comparer = null;
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
                    comparer = _comparerCodec.ReadValue(ref reader, header);
                    break;
                case 1:
                    result = new(InterfaceCollectionCodecHelpers.ReadCapacityHint(ref reader, header), comparer);
                    ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
                    break;
                case 2:
                    if (result is null)
                    {
                        result = new(comparer);
                        ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
                    }

                    result.Add(_elementCodec.ReadValue(ref reader, header));
                    break;
                default:
                    reader.ConsumeUnknownField(header);
                    break;
            }
        }

        if (result is null)
        {
            result = new(comparer);
            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
        }

        return result;
    }
}

internal abstract class DictionaryInterfaceCodec<TInterface, TKey, TValue> : IFieldCodec<TInterface>
    where TInterface : class, IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : notnull
{
    private static readonly Type FallbackType = typeof(Dictionary<TKey, TValue>);
    private static readonly Type KeyType = typeof(TKey);
    private static readonly Type ValueType = typeof(TValue);
    private readonly IFieldCodec<TKey> _keyCodec;
    private readonly IFieldCodec<TValue> _valueCodec;
    private readonly IFieldCodec<IEqualityComparer<TKey>> _comparerCodec;

    protected DictionaryInterfaceCodec(
        IFieldCodec<TKey> keyCodec,
        IFieldCodec<TValue> valueCodec,
        IFieldCodec<IEqualityComparer<TKey>> comparerCodec)
    {
        _keyCodec = OrleansGeneratedCodeHelper.UnwrapService(this, keyCodec);
        _valueCodec = OrleansGeneratedCodeHelper.UnwrapService(this, valueCodec);
        _comparerCodec = OrleansGeneratedCodeHelper.UnwrapService(this, comparerCodec);
    }

    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TInterface value) where TBufferWriter : IBufferWriter<byte>
    {
        if (InterfaceCollectionCodecHelpers.TryWriteRuntimeCodec(ref writer, fieldIdDelta, expectedType, value))
        {
            return;
        }

        var codecType = GetType();
        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, codecType, value))
        {
            return;
        }

        writer.WriteFieldHeader(fieldIdDelta, expectedType, codecType, WireType.TagDelimited);
        if (InterfaceCollectionCodecHelpers.TryGetCount(value, out var count))
        {
            WriteEntries(ref writer, value, count);
        }
        else
        {
            var snapshot = new List<KeyValuePair<TKey, TValue>>();
            foreach (var entry in value)
            {
                snapshot.Add(entry);
            }

            WriteEntries(ref writer, snapshot, snapshot.Count);
        }

        writer.WriteEndObject();
    }

    public TInterface ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (InterfaceCollectionCodecHelpers.TryReadReferenceOrSpecificCodec<TInterface, TInput>(ref reader, field, FallbackType, GetType(), out var result))
        {
            return result;
        }

        return (TInterface)(object)ReadFallback(ref reader, field);
    }

    private void WriteEntries<TBufferWriter>(ref Writer<TBufferWriter> writer, IEnumerable<KeyValuePair<TKey, TValue>> value, int count) where TBufferWriter : IBufferWriter<byte>
    {
        if (!InterfaceCollectionCodecHelpers.TryWriteCapacityHint(ref writer, 1, count))
        {
            return;
        }

        uint innerFieldIdDelta = 1;
        foreach (var entry in value)
        {
            _keyCodec.WriteField(ref writer, innerFieldIdDelta, KeyType, entry.Key);
            _valueCodec.WriteField(ref writer, 0, ValueType, entry.Value);
            innerFieldIdDelta = 0;
        }
    }

    private Dictionary<TKey, TValue> ReadFallback<TInput>(ref Reader<TInput> reader, Field field)
    {
        field.EnsureWireTypeTagDelimited();

        var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
        Dictionary<TKey, TValue> result = null;
        IEqualityComparer<TKey> comparer = null;
        TKey key = default;
        var valueExpected = false;
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
                    comparer = _comparerCodec.ReadValue(ref reader, header);
                    break;
                case 1:
                    result = new(InterfaceCollectionCodecHelpers.ReadCapacityHint(ref reader, header), comparer);
                    ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
                    break;
                case 2:
                    if (result is null)
                    {
                        result = new(comparer);
                        ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
                    }

                    if (!valueExpected)
                    {
                        key = _keyCodec.ReadValue(ref reader, header);
                        valueExpected = true;
                    }
                    else
                    {
                        result.Add(key, _valueCodec.ReadValue(ref reader, header));
                        valueExpected = false;
                    }

                    break;
                default:
                    reader.ConsumeUnknownField(header);
                    break;
            }
        }

        if (result is null)
        {
            result = new(comparer);
            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
        }

        return result;
    }
}

internal abstract class ListInterfaceCopier<TInterface, T>(IDeepCopierProvider copierProvider, IDeepCopier<T> elementCopier)
    : IDeepCopier<TInterface> where TInterface : class, IEnumerable<T>
{
    public TInterface DeepCopy(TInterface input, CopyContext context)
    {
        if (InterfaceCollectionCodecHelpers.TryCopyRuntime(copierProvider, input, context, out var runtimeResult))
        {
            return runtimeResult;
        }

        if (context.TryGetCopy<TInterface>(input, out var result))
        {
            return result;
        }

        var copy = InterfaceCollectionCodecHelpers.TryGetCount(input, out var count)
            ? new List<T>(InterfaceCollectionCodecHelpers.GetCapacityHint(count))
            : new List<T>();
        context.RecordCopy(input, copy);
        foreach (var element in input)
        {
            copy.Add(elementCopier.DeepCopy(element, context));
        }

        return (TInterface)(object)copy;
    }
}

internal abstract class SetInterfaceCopier<TInterface, T>(IDeepCopierProvider copierProvider, IDeepCopier<T> elementCopier) : IDeepCopier<TInterface> where TInterface : class, IEnumerable<T>
{
    public TInterface DeepCopy(TInterface input, CopyContext context)
    {
        if (InterfaceCollectionCodecHelpers.TryCopyRuntime(copierProvider, input, context, out var runtimeResult))
        {
            return runtimeResult;
        }

        if (context.TryGetCopy<TInterface>(input, out var result))
        {
            return result;
        }

        var copy = InterfaceCollectionCodecHelpers.TryGetCount(input, out var count)
            ? new HashSet<T>(InterfaceCollectionCodecHelpers.GetCapacityHint(count))
            : [];
        context.RecordCopy(input, copy);
        foreach (var element in input)
        {
            copy.Add(elementCopier.DeepCopy(element, context));
        }

        return (TInterface)(object)copy;
    }
}

internal abstract class DictionaryInterfaceCopier<TInterface, TKey, TValue>(
    IDeepCopierProvider copierProvider,
    IDeepCopier<TKey> keyCopier,
    IDeepCopier<TValue> valueCopier) : IDeepCopier<TInterface>
    where TInterface : class, IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : notnull
{
    public TInterface DeepCopy(TInterface input, CopyContext context)
    {
        if (InterfaceCollectionCodecHelpers.TryCopyRuntime(copierProvider, input, context, out var runtimeResult))
        {
            return runtimeResult;
        }

        if (context.TryGetCopy<TInterface>(input, out var result))
        {
            return result;
        }

        var copy = InterfaceCollectionCodecHelpers.TryGetCount(input, out var count)
            ? new Dictionary<TKey, TValue>(InterfaceCollectionCodecHelpers.GetCapacityHint(count))
            : [];
        context.RecordCopy(input, copy);
        foreach (var entry in input)
        {
            copy[keyCopier.DeepCopy(entry.Key, context)] = valueCopier.DeepCopy(entry.Value, context);
        }

        return (TInterface)(object)copy;
    }
}

[RegisterSerializer]
[Alias("EnumerableCodec`1")]
internal sealed class EnumerableCodec<T>(IFieldCodec<T> elementCodec)
    : ListInterfaceCodec<IEnumerable<T>, T>(elementCodec)
{
}

[RegisterCopier]
internal sealed class EnumerableCopier<T>(IDeepCopierProvider copierProvider, IDeepCopier<T> elementCopier)
    : ListInterfaceCopier<IEnumerable<T>, T>(copierProvider, elementCopier)
{
}

[RegisterSerializer]
[Alias("ReadOnlyCollectionInterfaceCodec`1")]
internal sealed class ReadOnlyCollectionInterfaceCodec<T>(IFieldCodec<T> elementCodec)
    : ListInterfaceCodec<IReadOnlyCollection<T>, T>(elementCodec)
{
}

[RegisterCopier]
internal sealed class ReadOnlyCollectionInterfaceCopier<T>(IDeepCopierProvider copierProvider, IDeepCopier<T> elementCopier)
    : ListInterfaceCopier<IReadOnlyCollection<T>, T>(copierProvider, elementCopier)
{
}

[RegisterSerializer]
[Alias("ReadOnlyListInterfaceCodec`1")]
internal sealed class ReadOnlyListInterfaceCodec<T>(IFieldCodec<T> elementCodec)
    : ListInterfaceCodec<IReadOnlyList<T>, T>(elementCodec)
{
}

[RegisterCopier]
internal sealed class ReadOnlyListInterfaceCopier<T>(IDeepCopierProvider copierProvider, IDeepCopier<T> elementCopier)
    : ListInterfaceCopier<IReadOnlyList<T>, T>(copierProvider, elementCopier)
{
}

[RegisterSerializer]
[Alias("CollectionInterfaceCodec`1")]
internal sealed class CollectionInterfaceCodec<T>(IFieldCodec<T> elementCodec)
    : ListInterfaceCodec<ICollection<T>, T>(elementCodec)
{
}

[RegisterCopier]
internal sealed class CollectionInterfaceCopier<T>(IDeepCopierProvider copierProvider, IDeepCopier<T> elementCopier)
    : ListInterfaceCopier<ICollection<T>, T>(copierProvider, elementCopier)
{
}

[RegisterSerializer]
[Alias("ListInterfaceCodec`1")]
internal sealed class ListInterfaceCodec<T>(IFieldCodec<T> elementCodec)
    : ListInterfaceCodec<IList<T>, T>(elementCodec)
{
}

[RegisterCopier]
internal sealed class ListInterfaceCopier<T>(IDeepCopierProvider copierProvider, IDeepCopier<T> elementCopier)
    : ListInterfaceCopier<IList<T>, T>(copierProvider, elementCopier)
{
}

[RegisterSerializer]
[Alias("SetInterfaceCodec`1")]
internal sealed class SetInterfaceCodec<T>(IFieldCodec<T> elementCodec, IFieldCodec<IEqualityComparer<T>> comparerCodec)
    : SetInterfaceCodec<ISet<T>, T>(elementCodec, comparerCodec)
{
}

[RegisterCopier]
internal sealed class SetInterfaceCopier<T>(IDeepCopierProvider copierProvider, IDeepCopier<T> elementCopier)
    : SetInterfaceCopier<ISet<T>, T>(copierProvider, elementCopier)
{
}

#if NET5_0_OR_GREATER
[RegisterSerializer]
[Alias("ReadOnlySetInterfaceCodec`1")]
internal sealed class ReadOnlySetInterfaceCodec<T>(IFieldCodec<T> elementCodec, IFieldCodec<IEqualityComparer<T>> comparerCodec)
    : SetInterfaceCodec<IReadOnlySet<T>, T>(elementCodec, comparerCodec)
{
}

[RegisterCopier]
internal sealed class ReadOnlySetInterfaceCopier<T>(IDeepCopierProvider copierProvider, IDeepCopier<T> elementCopier)
    : SetInterfaceCopier<IReadOnlySet<T>, T>(copierProvider, elementCopier)
{
}
#endif

[RegisterSerializer]
[Alias("DictionaryInterfaceCodec`2")]
internal sealed class DictionaryInterfaceCodec<TKey, TValue>(
    IFieldCodec<TKey> keyCodec,
    IFieldCodec<TValue> valueCodec,
    IFieldCodec<IEqualityComparer<TKey>> comparerCodec)
    : DictionaryInterfaceCodec<IDictionary<TKey, TValue>, TKey, TValue>(keyCodec, valueCodec, comparerCodec)
    where TKey : notnull
{
}

[RegisterCopier]
internal sealed class DictionaryInterfaceCopier<TKey, TValue>(
    IDeepCopierProvider copierProvider,
    IDeepCopier<TKey> keyCopier,
    IDeepCopier<TValue> valueCopier)
    : DictionaryInterfaceCopier<IDictionary<TKey, TValue>, TKey, TValue>(copierProvider, keyCopier, valueCopier)
    where TKey : notnull
{
}

[RegisterSerializer]
[Alias("ReadOnlyDictionaryInterfaceCodec`2")]
internal sealed class ReadOnlyDictionaryInterfaceCodec<TKey, TValue>(
    IFieldCodec<TKey> keyCodec,
    IFieldCodec<TValue> valueCodec,
    IFieldCodec<IEqualityComparer<TKey>> comparerCodec)
    : DictionaryInterfaceCodec<IReadOnlyDictionary<TKey, TValue>, TKey, TValue>(keyCodec, valueCodec, comparerCodec)
    where TKey : notnull
{
}

[RegisterCopier]
internal sealed class ReadOnlyDictionaryInterfaceCopier<TKey, TValue>(
    IDeepCopierProvider copierProvider,
    IDeepCopier<TKey> keyCopier,
    IDeepCopier<TValue> valueCopier)
    : DictionaryInterfaceCopier<IReadOnlyDictionary<TKey, TValue>, TKey, TValue>(copierProvider, keyCopier, valueCopier)
    where TKey : notnull
{
}
