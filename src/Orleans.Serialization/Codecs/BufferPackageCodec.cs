using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs;

/// <summary>Serializes a package structurally without consuming its owned buffer.</summary>
[RegisterSerializer]
public sealed class BufferPackageCodec : IFieldCodec<BufferPackage>
{
    private readonly IFieldCodec<string> _keyCodec;
    private readonly ArcBufferCodec _bufferCodec = new();

    /// <summary>Initializes a package codec.</summary>
    /// <param name="keyCodec">The codec for entry keys.</param>
    public BufferPackageCodec(IFieldCodec<string> keyCodec) => _keyCodec = keyCodec;

    /// <inheritdoc />
    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type? expectedType, BufferPackage? value)
        where TBufferWriter : IBufferWriter<byte>
    {
        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value)) return;
        ArgumentNullExceptionPolyfill.ThrowIfNull(value);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(BufferPackage), WireType.TagDelimited);
        _bufferCodec.WriteField(ref writer, 0, typeof(ArcBuffer), value.Buffer);
        UInt32Codec.WriteField(ref writer, 1, (uint)value.Count);
        uint delta = 1;
        foreach (var entry in value.Entries)
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(delta, null, null, WireType.TagDelimited);
            _keyCodec.WriteField(ref writer, 0, typeof(string), entry.Key);
            Int32Codec.WriteField(ref writer, 1, entry.Value.Offset);
            Int32Codec.WriteField(ref writer, 1, entry.Value.Length);
            writer.WriteEndObject();
            delta = 0;
        }

        writer.WriteEndObject();
    }

    /// <inheritdoc />
    [return: MaybeNull]
    public BufferPackage ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.WireType == WireType.Reference)
            return ReferenceCodec.ReadReference<BufferPackage, TInput>(ref reader, field);
        field.EnsureWireTypeTagDelimited();
        var referenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
        ArcBuffer buffer = default;
        BufferPackage? result = null;
        var entries = new Dictionary<string, (int Offset, int Length)>(StringComparer.Ordinal);
        var hasBuffer = false;
        int? count = null;
        uint fieldId = 0;
        try
        {
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndBaseOrEndObject) break;
                fieldId = checked(fieldId + header.FieldIdDelta);
                switch (fieldId)
                {
                    case 0:
                        if (hasBuffer) throw new InvalidOperationException("Duplicate package buffer field.");
                        buffer = _bufferCodec.ReadValue(ref reader, header);
                        hasBuffer = true;
                        break;
                    case 1:
                        if (count.HasValue) throw new InvalidOperationException("Duplicate package entry count.");
                        count = checked((int)UInt32Codec.ReadValue(ref reader, header));
                        reader.EnsureAvailable((uint)count.Value);
                        break;
                    case 2:
                        if (!count.HasValue || entries.Count >= count.Value)
                            throw new InvalidOperationException("Missing or incorrect package entry count.");
                        var entry = ReadEntry(ref reader, header);
                        entries.Add(entry.Key, (entry.Offset, entry.Length));
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            if (!hasBuffer || !count.HasValue || entries.Count != count.Value)
                throw new RequiredFieldMissingException("Package buffer or complete entry index is missing.");
            result = new BufferPackage(buffer, entries);
            buffer = default; // Ownership transferred only after validation succeeds.
            ReferenceCodec.RecordObject(reader.Session, result, referenceId);
            return result;
        }
        catch
        {
            result?.Release();
            throw;
        }
        finally
        {
            buffer.Dispose();
        }
    }

    private (string Key, int Offset, int Length) ReadEntry<TInput>(ref Reader<TInput> reader, Field field)
    {
        ReferenceCodec.MarkValueField(reader.Session);
        field.EnsureWireTypeTagDelimited();
        string? key = null;
        int? offset = null;
        int? length = null;
        uint fieldId = 0;
        while (true)
        {
            var header = reader.ReadFieldHeader();
            if (header.IsEndBaseOrEndObject) break;
            fieldId = checked(fieldId + header.FieldIdDelta);
            switch (fieldId)
            {
                case 0:
                    if (key is not null) throw new InvalidOperationException("Duplicate entry key field.");
                    key = _keyCodec.ReadValue(ref reader, header);
                    if (key is null) throw new InvalidOperationException("Package entry keys cannot be null.");
                    break;
                case 1:
                    if (offset.HasValue) throw new InvalidOperationException("Duplicate entry offset field.");
                    offset = Int32Codec.ReadValue(ref reader, header);
                    break;
                case 2:
                    if (length.HasValue) throw new InvalidOperationException("Duplicate entry length field.");
                    length = Int32Codec.ReadValue(ref reader, header);
                    break;
                default:
                    reader.ConsumeUnknownField(header);
                    break;
            }
        }

        if (key is null || !offset.HasValue || !length.HasValue)
            throw new RequiredFieldMissingException("Package entry key, offset, or length is missing.");
        return (key, offset.Value, length.Value);
    }
}

/// <summary>Copies package identity while acquiring an independent pin and sharing the read-only index.</summary>
[RegisterCopier]
public sealed class BufferPackageCopier : IDeepCopier<BufferPackage>
{
    /// <inheritdoc />
    [return: NotNullIfNotNull(nameof(input))]
    public BufferPackage? DeepCopy(BufferPackage? input, CopyContext context)
    {
        ArgumentNullExceptionPolyfill.ThrowIfNull(context);
        if (input is null) return null;
        if (context.TryGetCopy<BufferPackage>(input, out var result)) return result!;
        result = input.Retain();
        try { context.RecordCopy(input, result); return result; }
        catch { result.Release(); throw; }
    }
}
