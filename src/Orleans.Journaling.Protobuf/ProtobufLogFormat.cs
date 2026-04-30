using System.Buffers;
using Google.Protobuf;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling.Protobuf;

internal sealed class ProtobufLogFormat : ILogFormat
{
    private const uint StreamIdFieldNumber = 1;
    private const uint PayloadFieldNumber = 2;

    public ILogSegmentWriter CreateWriter()
        => new ProtobufLogSegmentWriter();

    public bool TryRead(ArcBufferReader input, ILogStreamStateMachineResolver resolver, bool isCompleted)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        if (input.Length == 0)
        {
            return false;
        }

        using var buffered = input.PeekSlice(input.Length);
        var remaining = buffered.AsReadOnlySequence();
        var reader = new SequenceReader<byte>(remaining);
        if (!ProtobufLogEntryReader.TryReadLengthPrefix(ref reader, offset: 0, isCompleted, out var messageLength, out _))
        {
            return false;
        }

        var prefixLength = reader.Consumed;
        if (messageLength == 0)
        {
            throw new InvalidOperationException(
                "Malformed protobuf log entry stream at byte offset 0: empty LogEntry messages are not valid.");
        }

        if (messageLength > (ulong)reader.Remaining)
        {
            if (!isCompleted)
            {
                return false;
            }

            throw new InvalidOperationException(
                $"Malformed protobuf log entry stream at byte offset 0: LogEntry length {messageLength} exceeds remaining input bytes {reader.Remaining}.");
        }

        var frameLength = checked(prefixLength + (long)messageLength);
        if (frameLength > int.MaxValue)
        {
            throw new InvalidOperationException("Malformed protobuf log entry stream: LogEntry exceeds maximum supported frame size.");
        }

        input.Skip(checked((int)frameLength));
        var message = remaining.Slice(prefixLength, messageLength);
        ProtobufLogEntryReader.ReadMessage(message, offset: 0, out var streamId, out var payload);
        var stateMachine = resolver.ResolveStateMachine(streamId);
        if (stateMachine is IFormattedLogEntryBuffer formattedEntryBuffer)
        {
            formattedEntryBuffer.AddFormattedEntry(new ProtobufFormattedLogEntry(payload));
        }
        else
        {
            stateMachine.Apply(payload);
        }

        return true;
    }

    private sealed class ProtobufLogSegmentWriter : LogSegmentWriterBase
    {
        private readonly ArcBufferWriter _buffer = new();
        private readonly ArcBufferWriter _payload = new();

        public override long Length => checked(_buffer.Length + (IsEntryActive ? GetCurrentFrameLength() : 0));

        public override ArcBuffer GetCommittedBuffer()
        {
            if (IsEntryActive)
            {
                throw new InvalidOperationException("The protobuf log segment has an active entry.");
            }

            return _buffer.PeekSlice(_buffer.Length);
        }

        public override void Reset()
        {
            if (IsEntryActive)
            {
                throw new InvalidOperationException("The protobuf log segment cannot be reset while an entry is active.");
            }

            _payload.Reset();
            _buffer.Reset();
        }

        public override void Dispose()
        {
            _payload.Dispose();
            _buffer.Dispose();
        }

        protected override void OnBeginEntry(LogStreamId streamId) => _payload.Reset();

        protected override int GetEntryStart(LogStreamId streamId) => _buffer.Length;

        protected override void AdvancePayload(int count) => _payload.AdvanceWriter(count);

        protected override Memory<byte> GetPayloadMemory(int sizeHint) => _payload.GetMemory(sizeHint);

        protected override Span<byte> GetPayloadSpan(int sizeHint) => _payload.GetSpan(sizeHint);

        protected override void WritePayload(ReadOnlySpan<byte> value) => _payload.Write(value);

        protected override void WritePayload(ReadOnlySequence<byte> value) => _payload.Write(value);

        protected override void CommitEntry(LogStreamId streamId, int entryStart)
        {
            var payloadLength = _payload.Length;
            using var payload = _payload.PeekSlice(payloadLength);
            var message = new ProtobufLogEntry();
            message.StreamId.Add(streamId.Value);
            message.Payload.Add(UnsafeByteOperations.UnsafeWrap(payload.AsReadOnlySequence().ToArray()));
            var messageLength = message.CalculateSize();
            ProtobufWire.WriteVarUInt32(_buffer, checked((uint)messageLength));
            message.WriteTo(_buffer);

            _payload.Reset();
        }

        protected override void AbortEntry(LogStreamId streamId, int entryStart) => _payload.Reset();

        protected override void OnAppendFormattedEntry(LogStreamId streamId, IFormattedLogEntry entry)
        {
            if (!OnTryAppendFormattedEntry(streamId, entry))
            {
                throw new InvalidOperationException(
                    $"The protobuf log writer cannot append formatted entry of type '{entry.GetType().FullName}'.");
            }
        }

        protected override bool OnTryAppendFormattedEntry(LogStreamId streamId, IFormattedLogEntry entry)
        {
            if (entry is not ProtobufFormattedLogEntry protobufEntry)
            {
                return false;
            }

            using var logEntry = CreateLogWriter(streamId).BeginEntry();
            logEntry.Writer.Write(protobufEntry.Payload.Span);
            logEntry.Commit();
            return true;
        }

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

    private sealed class ProtobufFormattedLogEntry : IFormattedLogEntry
    {
        public ProtobufFormattedLogEntry(ReadOnlySequence<byte> payload)
        {
            Payload = payload.ToArray();
        }

        public ReadOnlyMemory<byte> Payload { get; }
    }

    private static class ProtobufLogEntryReader
    {
        public static bool TryReadLengthPrefix(
            ref SequenceReader<byte> reader,
            long offset,
            bool isCompleted,
            out uint length,
            out int? minimumBufferLength)
            => TryReadVarUInt32(ref reader, offset, isCompleted, "LogEntry length prefix", out length, out minimumBufferLength);

        public static void ReadMessage(
            ReadOnlySequence<byte> message,
            long offset,
            out LogStreamId streamId,
            out ReadOnlySequence<byte> payload)
        {
            var logEntry = ProtobufGeneratedCodecHelpers.Parse(message, ProtobufLogEntry.Parser, "LogEntry");
            streamId = new(ProtobufGeneratedCodecHelpers.RequireStreamUInt64(logEntry.StreamId, "stream_id"));
            payload = new ReadOnlySequence<byte>(ProtobufGeneratedCodecHelpers.RequirePayload(logEntry.Payload).Memory);
        }

        private static bool TryReadVarUInt32(
            ref SequenceReader<byte> reader,
            long offset,
            bool isCompleted,
            string fieldName,
            out uint result,
            out int? minimumBufferLength)
        {
            result = 0;
            minimumBufferLength = null;
            for (var index = 0; index < 5; index++)
            {
                if (!reader.TryRead(out var value))
                {
                    if (!isCompleted)
                    {
                        minimumBufferLength = checked((int)reader.Consumed + 1);
                        return false;
                    }

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
                    return true;
                }
            }

            throw new InvalidOperationException(
                $"Malformed protobuf log entry stream at byte offset {offset}: malformed {fieldName}.");
        }
    }
}
