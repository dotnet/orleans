using System;
using System.Buffers;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.WireProtocol;

namespace Orleans.DurableMessaging;

internal abstract class DurableDeadLetterCodec<T>(
    IFieldCodec<DurableEnvelope> envelopes,
    IFieldCodec<DateTimeOffset> times,
    IFieldCodec<string> strings,
    IFieldCodec<int> integers) : IFieldCodec<T> where T : class
{
    protected abstract DurableEnvelope Envelope(T value);
    protected abstract DateTimeOffset Time(T value);
    protected abstract string Reason(T value);
    protected abstract int Attempts(T value);
    protected abstract T Retain(T value);
    protected abstract T Create(DurableEnvelope envelope, DateTimeOffset time, string reason, int attempts);

    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta,
        Type? expectedType, T? value) where TBufferWriter : IBufferWriter<byte>
    {
        if (value is null)
        {
            ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
            return;
        }
        // Dead letters are owning values, not reference-sharing containers.
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(T), WireType.TagDelimited);
        envelopes.WriteField(ref writer, 0, typeof(DurableEnvelope), Envelope(value));
        times.WriteField(ref writer, 1, typeof(DateTimeOffset), Time(value));
        strings.WriteField(ref writer, 1, typeof(string), Reason(value));
        integers.WriteField(ref writer, 1, typeof(int), Attempts(value));
        writer.WriteEndObject();
    }

    public T? ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.WireType == WireType.Reference)
        {
            var referenced = ReferenceCodec.ReadReference<T, TInput>(ref reader, field);
            return referenced is null ? null : Retain(referenced);
        }
        field.EnsureWireTypeTagDelimited();
        ReferenceCodec.MarkValueField(reader.Session);
        DurableEnvelope envelope = default;
        DateTimeOffset time = default;
        string reason = string.Empty;
        int attempts = 0;
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
                    case 0:
                        var next = envelopes.ReadValue(ref reader, header);
                        envelope.Dispose();
                        envelope = next;
                        break;
                    case 1: time = times.ReadValue(ref reader, header); break;
                    case 2: reason = strings.ReadValue(ref reader, header) ?? throw new InvalidOperationException("A dead letter reason must not be null."); break;
                    case 3: attempts = integers.ReadValue(ref reader, header); break;
                    default: reader.ConsumeUnknownField(header); break;
                }
            }
            return Create(envelope, time, reason, attempts);
        }
        catch
        {
            envelope.Dispose();
            throw;
        }
    }
}

[RegisterSerializer]
internal sealed class InboxDeadLetterCodec(IFieldCodec<DurableEnvelope> envelopes,
    IFieldCodec<DateTimeOffset> times, IFieldCodec<string> strings, IFieldCodec<int> integers)
    : DurableDeadLetterCodec<InboxDeadLetter>(envelopes, times, strings, integers)
{
    protected override InboxDeadLetter Retain(InboxDeadLetter value) => InboxDeadLetterLifecycle.RetainValue(value);
    protected override DurableEnvelope Envelope(InboxDeadLetter value) => value.Envelope;
    protected override DateTimeOffset Time(InboxDeadLetter value) => value.DeadLetteredAt;
    protected override string Reason(InboxDeadLetter value) => value.Reason;
    protected override int Attempts(InboxDeadLetter value) => value.AttemptCount;
    protected override InboxDeadLetter Create(DurableEnvelope envelope, DateTimeOffset time, string reason, int attempts) =>
        new() { Envelope = envelope, DeadLetteredAt = time, Reason = reason, AttemptCount = attempts };
}

[RegisterSerializer]
internal sealed class OutboxDeadLetterCodec(IFieldCodec<DurableEnvelope> envelopes,
    IFieldCodec<DateTimeOffset> times, IFieldCodec<string> strings, IFieldCodec<int> integers)
    : DurableDeadLetterCodec<OutboxDeadLetter>(envelopes, times, strings, integers)
{
    protected override OutboxDeadLetter Retain(OutboxDeadLetter value) => OutboxDeadLetterLifecycle.RetainValue(value);
    protected override DurableEnvelope Envelope(OutboxDeadLetter value) => value.Envelope;
    protected override DateTimeOffset Time(OutboxDeadLetter value) => value.DeadLetteredAt;
    protected override string Reason(OutboxDeadLetter value) => value.Reason;
    protected override int Attempts(OutboxDeadLetter value) => value.AttemptCount;
    protected override OutboxDeadLetter Create(DurableEnvelope envelope, DateTimeOffset time, string reason, int attempts) =>
        new() { Envelope = envelope, DeadLetteredAt = time, Reason = reason, AttemptCount = attempts };
}

[RegisterCopier]
internal sealed class InboxDeadLetterCopier : IDeepCopier<InboxDeadLetter>
{
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(input))]
    public InboxDeadLetter? DeepCopy(InboxDeadLetter? input, CopyContext context) =>
        input is null ? null : InboxDeadLetterLifecycle.RetainValue(input);
}

[RegisterCopier]
internal sealed class OutboxDeadLetterCopier : IDeepCopier<OutboxDeadLetter>
{
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(input))]
    public OutboxDeadLetter? DeepCopy(OutboxDeadLetter? input, CopyContext context) =>
        input is null ? null : OutboxDeadLetterLifecycle.RetainValue(input);
}
