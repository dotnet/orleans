using System.Buffers;
using System.Buffers.Binary;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling.MessagePack;

internal sealed class MessagePackLogFormat : IStateMachineLogFormat
{
    public IStateMachineLogExtentWriter CreateWriter()
        => new MessagePackLogExtentWriter();

    public void Read(ArcBuffer input, IStateMachineLogEntryConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        var reader = new SequenceReader<byte>(input.AsReadOnlySequence());
        while (!reader.End)
        {
            var offset = reader.Consumed;
            MessagePackLogEntryReader.ReadEntry(ref reader, offset, out var streamId, out var payload);
            consumer.OnEntry(streamId, payload);
        }
    }

    private sealed class MessagePackLogExtentWriter : StateMachineLogExtentWriterBase
    {
        private readonly ArcBufferWriter _buffer = new();
        private readonly ArcBufferWriter _payload = new();

        public override long Length => checked(_buffer.Length + (IsEntryActive ? GetCurrentFrameLength() : 0));

        public override ArcBuffer GetCommittedBuffer()
        {
            if (IsEntryActive)
            {
                throw new InvalidOperationException("The MessagePack log extent has an active entry.");
            }

            return _buffer.PeekSlice(_buffer.Length);
        }

        public override void Reset()
        {
            if (IsEntryActive)
            {
                throw new InvalidOperationException("The MessagePack log extent cannot be reset while an entry is active.");
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
            WriteByte(_buffer, 0x92);
            WriteUInt64(_buffer, streamId.Value);
            WriteBinHeader(_buffer, _payload.Length);

            if (_payload.Length > 0)
            {
                using var payload = _payload.PeekSlice(_payload.Length);
                _buffer.Write(payload.AsReadOnlySequence());
            }

            _payload.Reset();
        }

        protected override void AbortEntry(StateMachineId streamId, int entryStart) => _payload.Reset();

        private int GetCurrentFrameLength()
            => checked(1 + GetUInt64Size(ActiveStreamId.Value) + GetBinHeaderSize(_payload.Length) + _payload.Length);
    }

    private static class MessagePackLogEntryReader
    {
        public static void ReadEntry(
            ref SequenceReader<byte> reader,
            long offset,
            out StateMachineId streamId,
            out ReadOnlySequence<byte> payload)
        {
            var itemCount = ReadArrayHeader(ref reader, offset);
            if (itemCount != 2)
            {
                throw new InvalidOperationException(
                    $"Malformed MessagePack log entry stream at byte offset {offset}: expected entry array with 2 item(s), found {itemCount}.");
            }

            streamId = new(ReadUInt64(ref reader, offset, "streamId"));
            payload = ReadBin(ref reader, offset);
        }

        private static uint ReadArrayHeader(ref SequenceReader<byte> reader, long offset)
        {
            var code = ReadByte(ref reader, offset, "array header");
            if ((code & 0xF0) == 0x90)
            {
                return (uint)(code & 0x0F);
            }

            return code switch
            {
                0xDC => ReadUInt16BigEndian(ref reader, offset, "array16 length"),
                0xDD => ReadUInt32BigEndian(ref reader, offset, "array32 length"),
                _ => throw new InvalidOperationException(
                    $"Malformed MessagePack log entry stream at byte offset {offset}: expected entry array.")
            };
        }

        private static ulong ReadUInt64(ref SequenceReader<byte> reader, long offset, string fieldName)
        {
            var code = ReadByte(ref reader, offset, fieldName);
            if (code <= 0x7F)
            {
                return code;
            }

            return code switch
            {
                0xCC => ReadByte(ref reader, offset, fieldName),
                0xCD => ReadUInt16BigEndian(ref reader, offset, fieldName),
                0xCE => ReadUInt32BigEndian(ref reader, offset, fieldName),
                0xCF => ReadUInt64BigEndian(ref reader, offset, fieldName),
                _ => throw new InvalidOperationException(
                    $"Malformed MessagePack log entry stream at byte offset {offset}: streamId must be an unsigned integer.")
            };
        }

        private static ReadOnlySequence<byte> ReadBin(ref SequenceReader<byte> reader, long offset)
        {
            var code = ReadByte(ref reader, offset, "payload header");
            var length = code switch
            {
                0xC4 => ReadByte(ref reader, offset, "bin8 length"),
                0xC5 => ReadUInt16BigEndian(ref reader, offset, "bin16 length"),
                0xC6 => ReadUInt32BigEndian(ref reader, offset, "bin32 length"),
                _ => throw new InvalidOperationException(
                    $"Malformed MessagePack log entry stream at byte offset {offset}: payload must be a binary field.")
            };

            EnsureRemaining(ref reader, length, offset, "payload");
            var result = reader.Sequence.Slice(reader.Position, length);
            reader.Advance(length);
            return result;
        }

        private static byte ReadByte(ref SequenceReader<byte> reader, long offset, string fieldName)
        {
            if (!reader.TryRead(out var value))
            {
                throw new InvalidOperationException(
                    $"Malformed MessagePack log entry stream at byte offset {offset}: truncated {fieldName}.");
            }

            return value;
        }

        private static ushort ReadUInt16BigEndian(ref SequenceReader<byte> reader, long offset, string fieldName)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ushort)];
            if (!reader.TryCopyTo(bytes))
            {
                throw new InvalidOperationException(
                    $"Malformed MessagePack log entry stream at byte offset {offset}: truncated {fieldName}.");
            }

            reader.Advance(bytes.Length);
            return BinaryPrimitives.ReadUInt16BigEndian(bytes);
        }

        private static uint ReadUInt32BigEndian(ref SequenceReader<byte> reader, long offset, string fieldName)
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            if (!reader.TryCopyTo(bytes))
            {
                throw new InvalidOperationException(
                    $"Malformed MessagePack log entry stream at byte offset {offset}: truncated {fieldName}.");
            }

            reader.Advance(bytes.Length);
            return BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }

        private static ulong ReadUInt64BigEndian(ref SequenceReader<byte> reader, long offset, string fieldName)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            if (!reader.TryCopyTo(bytes))
            {
                throw new InvalidOperationException(
                    $"Malformed MessagePack log entry stream at byte offset {offset}: truncated {fieldName}.");
            }

            reader.Advance(bytes.Length);
            return BinaryPrimitives.ReadUInt64BigEndian(bytes);
        }

        private static void EnsureRemaining(ref SequenceReader<byte> reader, uint length, long offset, string fieldName)
        {
            if (length > (ulong)reader.Remaining)
            {
                throw new InvalidOperationException(
                    $"Malformed MessagePack log entry stream at byte offset {offset}: {fieldName} length {length} exceeds remaining input bytes {reader.Remaining}.");
            }
        }
    }

    private static int GetUInt64Size(ulong value)
    {
        if (value <= 0x7F)
        {
            return 1;
        }

        if (value <= byte.MaxValue)
        {
            return 2;
        }

        if (value <= ushort.MaxValue)
        {
            return 3;
        }

        if (value <= uint.MaxValue)
        {
            return 5;
        }

        return 9;
    }

    private static int GetBinHeaderSize(int length)
    {
        if (length <= byte.MaxValue)
        {
            return 2;
        }

        if (length <= ushort.MaxValue)
        {
            return 3;
        }

        return 5;
    }

    private static void WriteUInt64(IBufferWriter<byte> output, ulong value)
    {
        if (value <= 0x7F)
        {
            WriteByte(output, (byte)value);
        }
        else if (value <= byte.MaxValue)
        {
            var span = output.GetSpan(2);
            span[0] = 0xCC;
            span[1] = (byte)value;
            output.Advance(2);
        }
        else if (value <= ushort.MaxValue)
        {
            var span = output.GetSpan(3);
            span[0] = 0xCD;
            BinaryPrimitives.WriteUInt16BigEndian(span[1..], (ushort)value);
            output.Advance(3);
        }
        else if (value <= uint.MaxValue)
        {
            var span = output.GetSpan(5);
            span[0] = 0xCE;
            BinaryPrimitives.WriteUInt32BigEndian(span[1..], (uint)value);
            output.Advance(5);
        }
        else
        {
            var span = output.GetSpan(9);
            span[0] = 0xCF;
            BinaryPrimitives.WriteUInt64BigEndian(span[1..], value);
            output.Advance(9);
        }
    }

    private static void WriteBinHeader(IBufferWriter<byte> output, int length)
    {
        if (length <= byte.MaxValue)
        {
            var span = output.GetSpan(2);
            span[0] = 0xC4;
            span[1] = (byte)length;
            output.Advance(2);
        }
        else if (length <= ushort.MaxValue)
        {
            var span = output.GetSpan(3);
            span[0] = 0xC5;
            BinaryPrimitives.WriteUInt16BigEndian(span[1..], (ushort)length);
            output.Advance(3);
        }
        else
        {
            var span = output.GetSpan(5);
            span[0] = 0xC6;
            BinaryPrimitives.WriteUInt32BigEndian(span[1..], (uint)length);
            output.Advance(5);
        }
    }

    private static void WriteByte(IBufferWriter<byte> output, byte value)
    {
        var span = output.GetSpan(1);
        span[0] = value;
        output.Advance(1);
    }
}
