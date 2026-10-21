using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Runtime.Messaging;

/// <summary>
/// Encodes migration packages and their embedded messages, preserving lazy bodies and serialization ownership.
/// </summary>
[RegisterSerializer, RegisterCopier]
internal sealed class MessageCodec(
    IServiceProvider services,
    IFieldCodec<GrainId> grainIdCodec,
    IFieldCodec<MigrationContext> contextCodec,
    IFieldCodec<List<Message>> requestsCodec) : IFieldCodec<Message>, IFieldCodec<GrainMigrationPackage>, IDeepCopier<GrainMigrationPackage>
{
    private MessageHandlerShared? _shared;

    // Resolve after codec construction: message handlers themselves depend on the codec provider.
    private MessageHandlerShared Shared => _shared ??= services.GetRequiredService<MessageHandlerShared>();

    void IFieldCodec.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type? expectedType, object? value)
    {
        if (value is GrainMigrationPackage package)
        {
            ((IFieldCodec<GrainMigrationPackage>)this).WriteField(ref writer, fieldIdDelta, expectedType, package);
        }
        else
        {
            WriteField(ref writer, fieldIdDelta, expectedType, (Message?)value);
        }
    }

    object? IFieldCodec.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        => field.WireType == WireType.TagDelimited
            ? ((IFieldCodec<GrainMigrationPackage>)this).ReadValue(ref reader, field)
            : ReadValue(ref reader, field);

    void IFieldCodec<GrainMigrationPackage>.WriteField<TBufferWriter>(
        ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type? expectedType, GrainMigrationPackage value)
    {
        value.Source?.BeginSerialization();
        try
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteStartObject(fieldIdDelta, expectedType, typeof(GrainMigrationPackage));
            grainIdCodec.WriteField(ref writer, 0, typeof(GrainId), value.GrainId);
            contextCodec.WriteField(ref writer, 1, typeof(MigrationContext), value.MigrationContext);
            requestsCodec.WriteField(ref writer, 1, typeof(List<Message>), value.Requests);
            writer.WriteEndObject();
        }
        finally
        {
            if (value.Source is not null)
            {
                value.ReleasePayload();
            }
        }
    }

    GrainMigrationPackage IFieldCodec<GrainMigrationPackage>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        field.EnsureWireTypeTagDelimited();
        ReferenceCodec.MarkValueField(reader.Session);
        var result = new GrainMigrationPackage();
        uint id = 0;
        while (true)
        {
            var header = reader.ReadFieldHeader();
            if (header.IsEndBaseOrEndObject)
            {
                // Failed-RPC cleanup still owns the receive slices while package structure is validated.
                if (result.MigrationContext is null)
                {
                    throw new InvalidMessageFrameException("A migration package must contain a migration context.");
                }

                if (result.Requests?.Exists(static request => request is null) == true)
                {
                    throw new InvalidMessageFrameException("A migration package cannot contain a null request.");
                }

                return result;
            }

            id += header.FieldIdDelta;
            switch (id)
            {
                case 0:
                    result.GrainId = grainIdCodec.ReadValue(ref reader, header);
                    break;
                case 1:
                    result.MigrationContext = contextCodec.ReadValue(ref reader, header)!;
                    break;
                case 2:
                    result.Requests = requestsCodec.ReadValue(ref reader, header);
                    break;
                default:
                    reader.ConsumeUnknownField(header);
                    break;
            }
        }
    }

    public GrainMigrationPackage DeepCopy(GrainMigrationPackage input, CopyContext context) => input;

    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type? expectedType, Message? value)
        where TBufferWriter : IBufferWriter<byte>
    {
        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
        {
            return;
        }

        var shared = Shared;
        var serializer = shared.GetMessageSerializer();
        try
        {
            using var buffer = new ArcBufferWriter();
            MessageFrames.Write(serializer, buffer, value);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(Message), WireType.LengthPrefixed);
            writer.WriteVarUInt32((uint)buffer.Length);
            using var frame = buffer.ConsumeSlice(buffer.Length);
            foreach (var span in frame)
            {
                writer.Write(span);
            }
        }
        finally
        {
            shared.Return(serializer);
        }
    }

    [return: MaybeNull]
    public Message ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.WireType == WireType.Reference)
        {
            return ReferenceCodec.ReadReference<Message, TInput>(ref reader, field);
        }

        field.EnsureWireType(WireType.LengthPrefixed);
        var length = checked((int)reader.ReadVarUInt32());
        using var frame = reader.ReadOwnedBuffer(length);
        var result = MessageFrames.Read(Shared, frame);
        ReferenceCodec.RecordObject(reader.Session, result);
        return result;
    }
}
