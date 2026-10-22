using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Networking.Shared;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Runtime.Messaging;

/// <summary>
/// Encodes migrating requests using the ordinary eager-body message serializer.
/// </summary>
[RegisterSerializer, RegisterCopier]
internal sealed class MessageCodec(
    IServiceProvider services,
    IFieldCodec<GrainId> grainIdCodec,
    IFieldCodec<MigrationContext> contextCodec,
    IFieldCodec<List<Message>> requestsCodec) : IFieldCodec<Message>, IFieldCodec<GrainMigrationPackage>, IDeepCopier<GrainMigrationPackage>
{
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
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteStartObject(fieldIdDelta, expectedType, typeof(GrainMigrationPackage));
        grainIdCodec.WriteField(ref writer, 0, typeof(GrainId), value.GrainId);
        contextCodec.WriteField(ref writer, 1, typeof(MigrationContext), value.MigrationContext);
        if (value is { Requests: { } requests, Source.Frames: { } frames })
        {
            // Match ListCodec<Message>'s wire shape while reusing the prepared frames.
            if (!ReferenceCodec.TryWriteReferenceField(ref writer, 1, typeof(List<Message>), requests))
            {
                writer.WriteStartObject(1, typeof(List<Message>), typeof(List<Message>));
                if (requests.Count > 0)
                {
                    UInt32Codec.WriteField(ref writer, 0, (uint)requests.Count);
                    for (var i = 0; i < requests.Count; i++)
                    {
                        var delta = i == 0 ? 1u : 0u;
                        if (!ReferenceCodec.TryWriteReferenceField(ref writer, delta, typeof(Message), requests[i]))
                        {
                            WriteFrame(ref writer, delta, typeof(Message), frames[i]);
                        }
                    }
                }

                writer.WriteEndObject();
            }
        }
        else
        {
            requestsCodec.WriteField(ref writer, 1, typeof(List<Message>), value.Requests);
        }

        writer.WriteEndObject();
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

        using var serializer = CreateMessageSerializer(services);
        WriteFrame(ref writer, fieldIdDelta, expectedType, PrepareFrame(serializer, value));
    }

    private static void WriteFrame<TBufferWriter>(
        ref Writer<TBufferWriter> writer,
        uint fieldIdDelta,
        Type? expectedType,
        byte[] frame) where TBufferWriter : IBufferWriter<byte>
    {
        writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(Message), WireType.LengthPrefixed);
        writer.WriteVarUInt32((uint)frame.Length);
        writer.Write(frame);
    }

    internal static byte[] PrepareFrame(MessageSerializer serializer, Message message)
    {
        var pipe = new Pipe();
        try
        {
            serializer.Write(pipe.Writer, message);
            pipe.Writer.Complete();
            if (!pipe.Reader.TryRead(out var result))
            {
                throw new InvalidMessageFrameException("The migration request frame is missing.");
            }

            var bytes = result.Buffer.ToArray();
            pipe.Reader.AdvanceTo(result.Buffer.End);
            return bytes;
        }
        finally
        {
            pipe.Writer.Complete();
            pipe.Reader.Complete();
        }
    }

    // The legacy framer owns mutable sessions and buffers. Construct it explicitly so its
    // lifetime belongs to this operation, while its dependencies remain singleton services.
    internal static MessageSerializer CreateMessageSerializer(IServiceProvider services)
        => new(
            services.GetRequiredService<SerializerSessionPool>(),
            services.GetRequiredService<SharedMemoryPool>(),
            services.GetRequiredService<IOptions<SiloMessagingOptions>>().Value);

    internal static Message ReadFrame(MessageSerializer serializer, ReadOnlySequence<byte> input)
    {
        var (requiredBytes, _, _) = serializer.TryRead(ref input, out var message);
        if (requiredBytes != 0 || message is null || !input.IsEmpty)
        {
            throw new InvalidMessageFrameException("The migration request must contain exactly one complete message frame.");
        }

        return message;
    }

    [return: MaybeNull]
    public Message ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.WireType == WireType.Reference)
        {
            return ReferenceCodec.ReadReference<Message, TInput>(ref reader, field);
        }

        field.EnsureWireType(WireType.LengthPrefixed);
        var length = reader.ReadVarUInt32();
        using var serializer = CreateMessageSerializer(services);
        var result = ReadFrame(serializer, new(reader.ReadBytes(length)));
        ReferenceCodec.RecordObject(reader.Session, result);
        return result;
    }
}
