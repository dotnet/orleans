using System;
using System.Buffers;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.WireProtocol;

namespace Orleans.DurableMessaging;

// Writes borrow ownership, including journal writes and snapshots. Reads transfer one
// owned payload to the result and release it if a subsequent field cannot be read.
[RegisterSerializer]
internal sealed class DurableEnvelopeCodec(
    IFieldCodec<Guid> ids,
    IFieldCodec<GrainId> grains,
    IFieldCodec<ArcBuffer> buffers) : IFieldCodec<DurableEnvelope>
{
    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta,
        Type? expectedType, DurableEnvelope value) where TBufferWriter : IBufferWriter<byte>
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(DurableEnvelope), WireType.TagDelimited);
        ids.WriteField(ref writer, 0, typeof(Guid), value.MessageId);
        grains.WriteField(ref writer, 1, typeof(GrainId), value.SenderId);
        grains.WriteField(ref writer, 1, typeof(GrainId), value.ReceiverId);
        buffers.WriteField(ref writer, 1, typeof(ArcBuffer), value.Payload);
        writer.WriteEndObject();
    }

    public DurableEnvelope ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        field.EnsureWireTypeTagDelimited();
        ReferenceCodec.MarkValueField(reader.Session);
        Guid messageId = default;
        GrainId sender = default, receiver = default;
        ArcBuffer payload = default;
        uint fieldId = 0;
        try
        {
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndBaseOrEndObject) break;
                fieldId += header.FieldIdDelta;
                switch (fieldId)
                {
                    case 0: messageId = ids.ReadValue(ref reader, header); break;
                    case 1: sender = grains.ReadValue(ref reader, header); break;
                    case 2: receiver = grains.ReadValue(ref reader, header); break;
                    case 3:
                        var next = buffers.ReadValue(ref reader, header);
                        payload.Dispose();
                        payload = next;
                        break;
                    default: reader.ConsumeUnknownField(header); break;
                }
            }
            return new DurableEnvelope { MessageId = messageId, SenderId = sender, ReceiverId = receiver, Payload = payload };
        }
        catch
        {
            payload.Dispose();
            throw;
        }
    }
}

[RegisterCopier]
internal sealed class DurableEnvelopeCopier : IDeepCopier<DurableEnvelope>
{
    public DurableEnvelope DeepCopy(DurableEnvelope input, CopyContext context) => input.Retain();
}
