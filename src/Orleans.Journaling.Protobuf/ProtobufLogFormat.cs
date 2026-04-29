using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling.Protobuf;

internal sealed class ProtobufLogFormat : IStateMachineLogFormat
{
    private const uint StreamIdFieldNumber = 1;
    private const uint PayloadFieldNumber = 2;

    public IStateMachineLogExtentWriter CreateWriter()
        => new ProtobufLogExtentWriter();

    public void Read(ArcBuffer input, IStateMachineLogEntryConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        var remaining = input.AsReadOnlySequence();
        var offset = 0L;
        while (!remaining.IsEmpty)
        {
            var reader = new SequenceReader<byte>(remaining);
            var messageLength = ProtobufLogEntryReader.ReadLengthPrefix(ref reader, offset);
            var prefixLength = reader.Consumed;
            if (messageLength == 0)
            {
                throw new InvalidOperationException(
                    $"Malformed protobuf log entry stream at byte offset {offset}: empty LogEntry messages are not valid.");
            }

            if (messageLength > (ulong)reader.Remaining)
            {
                throw new InvalidOperationException(
                    $"Malformed protobuf log entry stream at byte offset {offset}: LogEntry length {messageLength} exceeds remaining input bytes {reader.Remaining}.");
            }

            var message = remaining.Slice(prefixLength, messageLength);
            ProtobufLogEntryReader.ReadMessage(message, offset, out var streamId, out var payload);
            consumer.OnEntry(streamId, payload);

            var frameLength = checked(prefixLength + (long)messageLength);
            remaining = remaining.Slice(frameLength);
            offset += frameLength;
        }
    }

    private sealed class ProtobufLogExtentWriter : StateMachineLogExtentWriterBase
    {
        private readonly ArcBufferWriter _buffer = new();
        private readonly ArcBufferWriter _payload = new();

        public override long Length => checked(_buffer.Length + (IsEntryActive ? GetCurrentFrameLength() : 0));

        public override ArcBuffer GetCommittedBuffer()
        {
            if (IsEntryActive)
            {
                throw new InvalidOperationException("The protobuf log extent has an active entry.");
            }

            return _buffer.PeekSlice(_buffer.Length);
        }

        public override void Reset()
        {
            if (IsEntryActive)
            {
                throw new InvalidOperationException("The protobuf log extent cannot be reset while an entry is active.");
            }

            _payload.Reset();
            _buffer.Reset();
        }

        public override void Dispose()
        {
            _payload.Dispose();
            _buffer.Dispose();
        }

        protected override void OnBeginEntry(StateMachineId streamId) => _payload.Reset();

        protected override int GetEntryStart(StateMachineId streamId) => _buffer.Length;

        protected override void AdvancePayload(int count) => _payload.AdvanceWriter(count);

        protected override Memory<byte> GetPayloadMemory(int sizeHint) => _payload.GetMemory(sizeHint);

        protected override Span<byte> GetPayloadSpan(int sizeHint) => _payload.GetSpan(sizeHint);

        protected override void WritePayload(ReadOnlySpan<byte> value) => _payload.Write(value);

        protected override void WritePayload(ReadOnlySequence<byte> value) => _payload.Write(value);

        protected override void CommitEntry(StateMachineId streamId, int entryStart)
        {
            var payloadLength = _payload.Length;
            var messageLength = GetMessageBodyLength(streamId.Value, payloadLength);
            ProtobufWire.WriteVarUInt32(_buffer, checked((uint)messageLength));
            ProtobufWire.WriteUInt64Field(_buffer, StreamIdFieldNumber, streamId.Value);
            ProtobufWire.WriteTag(_buffer, PayloadFieldNumber, ProtobufWire.WireTypeLengthDelimited);
            ProtobufWire.WriteVarUInt32(_buffer, checked((uint)payloadLength));

            if (payloadLength > 0)
            {
                using var payload = _payload.PeekSlice(payloadLength);
                _buffer.Write(payload.AsReadOnlySequence());
            }

            _payload.Reset();
        }

        protected override void AbortEntry(StateMachineId streamId, int entryStart) => _payload.Reset();

        private int GetCurrentFrameLength()
        {
            var messageLength = GetMessageBodyLength(ActiveStreamId.Value, _payload.Length);
            return checked(ProtobufWire.ComputeVarUInt32Size((uint)messageLength) + messageLength);
        }

        private static int GetMessageBodyLength(ulong streamId, int payloadLength)
            => checked(
                ProtobufWire.ComputeVarUInt32Size((StreamIdFieldNumber << 3) | ProtobufWire.WireTypeVarint) +
                ProtobufWire.ComputeVarUInt64Size(streamId) +
                ProtobufWire.ComputeVarUInt32Size((PayloadFieldNumber << 3) | ProtobufWire.WireTypeLengthDelimited) +
                ProtobufWire.ComputeVarUInt32Size((uint)payloadLength) +
                payloadLength);
    }

    private static class ProtobufLogEntryReader
    {
        public static uint ReadLengthPrefix(ref SequenceReader<byte> reader, long offset)
            => ReadVarUInt32(ref reader, offset, "LogEntry length prefix");

        public static void ReadMessage(
            ReadOnlySequence<byte> message,
            long offset,
            out StateMachineId streamId,
            out ReadOnlySequence<byte> payload)
        {
            var reader = new SequenceReader<byte>(message);
            ulong id = 0;
            var hasStreamId = false;
            payload = default;
            var hasPayload = false;

            while (!reader.End)
            {
                var tag = ReadVarUInt32(ref reader, offset, "field tag");
                if (tag == 0)
                {
                    throw new InvalidOperationException(
                        $"Malformed protobuf log entry stream at byte offset {offset}: field number 0 is not valid.");
                }

                var fieldNumber = tag >> 3;
                var wireType = tag & 0b111;
                switch (fieldNumber)
                {
                    case StreamIdFieldNumber:
                        if (wireType != ProtobufWire.WireTypeVarint)
                        {
                            throw new InvalidOperationException(
                                $"Malformed protobuf log entry stream at byte offset {offset}: stream_id must be a varint field.");
                        }

                        if (hasStreamId)
                        {
                            throw new InvalidOperationException(
                                $"Malformed protobuf log entry stream at byte offset {offset}: duplicate stream_id field.");
                        }

                        id = ReadVarUInt64(ref reader, offset, "stream_id");
                        hasStreamId = true;
                        break;
                    case PayloadFieldNumber:
                        if (wireType != ProtobufWire.WireTypeLengthDelimited)
                        {
                            throw new InvalidOperationException(
                                $"Malformed protobuf log entry stream at byte offset {offset}: payload must be a bytes field.");
                        }

                        if (hasPayload)
                        {
                            throw new InvalidOperationException(
                                $"Malformed protobuf log entry stream at byte offset {offset}: duplicate payload field.");
                        }

                        payload = ReadBytes(ref reader, offset, "payload");
                        hasPayload = true;
                        break;
                    default:
                        SkipField(ref reader, wireType, offset);
                        break;
                }
            }

            if (!hasStreamId)
            {
                throw new InvalidOperationException(
                    $"Malformed protobuf log entry stream at byte offset {offset}: missing required stream_id field.");
            }

            if (!hasPayload)
            {
                throw new InvalidOperationException(
                    $"Malformed protobuf log entry stream at byte offset {offset}: missing required payload field.");
            }

            streamId = new(id);
        }

        private static ReadOnlySequence<byte> ReadBytes(ref SequenceReader<byte> reader, long offset, string fieldName)
        {
            var length = ReadVarUInt32(ref reader, offset, fieldName);
            EnsureRemaining(ref reader, length, offset, fieldName);
            var result = reader.Sequence.Slice(reader.Position, length);
            reader.Advance(length);
            return result;
        }

        private static void SkipField(ref SequenceReader<byte> reader, uint wireType, long offset)
        {
            switch (wireType)
            {
                case ProtobufWire.WireTypeVarint:
                    _ = ReadVarUInt64(ref reader, offset, "unknown varint field");
                    break;
                case ProtobufWire.WireTypeFixed64:
                    EnsureRemaining(ref reader, 8, offset, "unknown fixed64 field");
                    reader.Advance(8);
                    break;
                case ProtobufWire.WireTypeLengthDelimited:
                    var length = ReadVarUInt32(ref reader, offset, "unknown length-delimited field");
                    EnsureRemaining(ref reader, length, offset, "unknown length-delimited field");
                    reader.Advance(length);
                    break;
                case ProtobufWire.WireTypeFixed32:
                    EnsureRemaining(ref reader, 4, offset, "unknown fixed32 field");
                    reader.Advance(4);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Malformed protobuf log entry stream at byte offset {offset}: unsupported wire type {wireType}.");
            }
        }

        private static uint ReadVarUInt32(ref SequenceReader<byte> reader, long offset, string fieldName)
        {
            uint result = 0;
            for (var index = 0; index < 5; index++)
            {
                if (!reader.TryRead(out var value))
                {
                    throw new InvalidOperationException(
                        $"Malformed protobuf log entry stream at byte offset {offset}: truncated {fieldName}.");
                }

                if (index == 4 && (value & 0xF0) != 0)
                {
                    throw new InvalidOperationException(
                        $"Malformed protobuf log entry stream at byte offset {offset}: malformed {fieldName}.");
                }

                result |= (uint)(value & 0x7F) << (index * 7);
                if ((value & 0x80) == 0)
                {
                    return result;
                }
            }

            throw new InvalidOperationException(
                $"Malformed protobuf log entry stream at byte offset {offset}: malformed {fieldName}.");
        }

        private static ulong ReadVarUInt64(ref SequenceReader<byte> reader, long offset, string fieldName)
        {
            ulong result = 0;
            for (var index = 0; index < 10; index++)
            {
                if (!reader.TryRead(out var value))
                {
                    throw new InvalidOperationException(
                        $"Malformed protobuf log entry stream at byte offset {offset}: truncated {fieldName}.");
                }

                var valueBits = value & 0x7F;
                if (index == 9 && (valueBits > 1 || (value & 0x80) != 0))
                {
                    throw new InvalidOperationException(
                        $"Malformed protobuf log entry stream at byte offset {offset}: malformed {fieldName}.");
                }

                result |= (ulong)valueBits << (index * 7);
                if ((value & 0x80) == 0)
                {
                    return result;
                }
            }

            throw new InvalidOperationException(
                $"Malformed protobuf log entry stream at byte offset {offset}: malformed {fieldName}.");
        }

        private static void EnsureRemaining(ref SequenceReader<byte> reader, uint length, long offset, string fieldName)
        {
            if (length > (ulong)reader.Remaining)
            {
                throw new InvalidOperationException(
                    $"Malformed protobuf log entry stream at byte offset {offset}: {fieldName} length {length} exceeds remaining input bytes {reader.Remaining}.");
            }
        }
    }
}
