using System.Buffers;
using System.Buffers.Binary;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling.MessagePack;

internal sealed class MessagePackLogFormat : ILogFormat
{
    public ILogBatchWriter CreateWriter()
        => new MessagePackLogSegmentWriter();

    public void Read(LogReadBuffer input, IStateMachineResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        while (TryReadEntry(input, resolver))
        {
        }
    }

    private static bool TryReadEntry(LogReadBuffer input, IStateMachineResolver resolver)
    {
        if (input.Length == 0)
        {
            return false;
        }

        using var buffered = input.PeekSlice(input.Length);
        var reader = new SequenceReader<byte>(buffered.AsReadOnlySequence());
        if (!MessagePackLogEntryReader.TryReadEntry(ref reader, offset: 0, input.IsCompleted, out var streamId, out var payload, out _))
        {
            return false;
        }

        if (reader.Consumed > int.MaxValue)
        {
            throw new InvalidOperationException("Malformed MessagePack log entry stream: log entry exceeds maximum supported frame size.");
        }

        input.Skip(checked((int)reader.Consumed));
        var stateMachine = resolver.ResolveStateMachine(streamId);
        if (stateMachine is IFormattedLogEntryBuffer formattedEntryBuffer)
        {
            formattedEntryBuffer.AddFormattedEntry(new MessagePackFormattedLogEntry(payload));
        }
        else
        {
            stateMachine.Apply(payload);
        }

        return true;
    }

    private sealed class MessagePackLogSegmentWriter : LogBatchWriterBase
    {
        private readonly ArcBufferWriter _buffer = new();
        private readonly ArcBufferWriter _payload = new();

        public override long Length => checked(_buffer.Length + (IsEntryActive ? GetCurrentFrameLength() : 0));

        public override ArcBuffer GetCommittedBuffer()
        {
            if (IsEntryActive)
            {
                throw new InvalidOperationException("The MessagePack log segment has an active entry.");
            }

            return _buffer.PeekSlice(_buffer.Length);
        }

        public override void Reset()
        {
            if (IsEntryActive)
            {
                throw new InvalidOperationException("The MessagePack log segment cannot be reset while an entry is active.");
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

        protected override void AbortEntry(LogStreamId streamId, int entryStart) => _payload.Reset();

        protected override void OnAppendFormattedEntry(LogStreamId streamId, IFormattedLogEntry entry)
        {
            if (!OnTryAppendFormattedEntry(streamId, entry))
            {
                throw new InvalidOperationException(
                    $"The MessagePack log writer cannot append formatted entry of type '{entry.GetType().FullName}'.");
            }
        }

        protected override bool OnTryAppendFormattedEntry(LogStreamId streamId, IFormattedLogEntry entry)
        {
            if (entry is not MessagePackFormattedLogEntry messagePackEntry)
            {
                return false;
            }

            using var logEntry = CreateLogStreamWriter(streamId).BeginEntry();
            logEntry.Writer.Write(messagePackEntry.Payload.Span);
            logEntry.Commit();
            return true;
        }

        private int GetCurrentFrameLength()
            => checked(1 + GetUInt64Size(ActiveStreamId.Value) + GetBinHeaderSize(_payload.Length) + _payload.Length);
    }

    private sealed class MessagePackFormattedLogEntry : IFormattedLogEntry
    {
        public MessagePackFormattedLogEntry(ReadOnlySequence<byte> payload)
        {
            Payload = payload.ToArray();
        }

        public ReadOnlyMemory<byte> Payload { get; }
    }

    private static class MessagePackLogEntryReader
    {
        public static bool TryReadEntry(
            ref SequenceReader<byte> reader,
            long offset,
            bool isCompleted,
            out LogStreamId streamId,
            out ReadOnlySequence<byte> payload,
            out int? minimumBufferLength)
        {
            var start = reader;
            streamId = default;
            payload = default;
            minimumBufferLength = null;

            if (!TryReadArrayHeader(ref reader, offset, isCompleted, out var itemCount, out minimumBufferLength))
            {
                reader = start;
                return false;
            }

            if (itemCount != 2)
            {
                throw new InvalidOperationException(
                    $"Malformed MessagePack log entry stream at byte offset {offset}: expected entry array with 2 item(s), found {itemCount}.");
            }

            if (!TryReadUInt64(ref reader, offset, isCompleted, "streamId", out var id, out minimumBufferLength)
                || !TryReadBin(ref reader, offset, isCompleted, out payload, out minimumBufferLength))
            {
                reader = start;
                return false;
            }

            streamId = new(id);
            return true;
        }

        private static bool TryReadArrayHeader(
            ref SequenceReader<byte> reader,
            long offset,
            bool isCompleted,
            out uint result,
            out int? minimumBufferLength)
        {
            result = 0;
            if (!TryReadByte(ref reader, offset, isCompleted, "array header", out var code, out minimumBufferLength))
            {
                return false;
            }

            if ((code & 0xF0) == 0x90)
            {
                result = (uint)(code & 0x0F);
                return true;
            }

            switch (code)
            {
                case 0xDC:
                    if (!TryReadUInt16BigEndian(ref reader, offset, isCompleted, "array16 length", out var array16Length, out minimumBufferLength))
                    {
                        return false;
                    }

                    result = array16Length;
                    return true;
                case 0xDD:
                    return TryReadUInt32BigEndian(ref reader, offset, isCompleted, "array32 length", out result, out minimumBufferLength);
                default:
                    throw new InvalidOperationException(
                        $"Malformed MessagePack log entry stream at byte offset {offset}: expected entry array.");
            }
        }

        private static bool TryReadUInt64(
            ref SequenceReader<byte> reader,
            long offset,
            bool isCompleted,
            string fieldName,
            out ulong result,
            out int? minimumBufferLength)
        {
            result = 0;
            if (!TryReadByte(ref reader, offset, isCompleted, fieldName, out var code, out minimumBufferLength))
            {
                return false;
            }

            if (code <= 0x7F)
            {
                result = code;
                return true;
            }

            switch (code)
            {
                case 0xCC:
                    if (!TryReadByte(ref reader, offset, isCompleted, fieldName, out var byteValue, out minimumBufferLength))
                    {
                        return false;
                    }

                    result = byteValue;
                    return true;
                case 0xCD:
                    if (!TryReadUInt16BigEndian(ref reader, offset, isCompleted, fieldName, out var ushortValue, out minimumBufferLength))
                    {
                        return false;
                    }

                    result = ushortValue;
                    return true;
                case 0xCE:
                    if (!TryReadUInt32BigEndian(ref reader, offset, isCompleted, fieldName, out var uintValue, out minimumBufferLength))
                    {
                        return false;
                    }

                    result = uintValue;
                    return true;
                case 0xCF:
                    return TryReadUInt64BigEndian(ref reader, offset, isCompleted, fieldName, out result, out minimumBufferLength);
                default:
                    throw new InvalidOperationException(
                        $"Malformed MessagePack log entry stream at byte offset {offset}: streamId must be an unsigned integer.");
            }
        }

        private static bool TryReadBin(
            ref SequenceReader<byte> reader,
            long offset,
            bool isCompleted,
            out ReadOnlySequence<byte> result,
            out int? minimumBufferLength)
        {
            result = default;
            if (!TryReadByte(ref reader, offset, isCompleted, "payload header", out var code, out minimumBufferLength))
            {
                return false;
            }

            uint length;
            switch (code)
            {
                case 0xC4:
                    if (!TryReadByte(ref reader, offset, isCompleted, "bin8 length", out var bin8Length, out minimumBufferLength))
                    {
                        return false;
                    }

                    length = bin8Length;
                    break;
                case 0xC5:
                    if (!TryReadUInt16BigEndian(ref reader, offset, isCompleted, "bin16 length", out var bin16Length, out minimumBufferLength))
                    {
                        return false;
                    }

                    length = bin16Length;
                    break;
                case 0xC6:
                    if (!TryReadUInt32BigEndian(ref reader, offset, isCompleted, "bin32 length", out length, out minimumBufferLength))
                    {
                        return false;
                    }

                    break;
                default:
                    throw new InvalidOperationException(
                        $"Malformed MessagePack log entry stream at byte offset {offset}: payload must be a binary field.");
            }

            if (!TryEnsureRemaining(ref reader, length, offset, isCompleted, "payload", out minimumBufferLength))
            {
                return false;
            }

            result = reader.Sequence.Slice(reader.Position, length);
            reader.Advance(length);
            return true;
        }

        private static bool TryReadByte(
            ref SequenceReader<byte> reader,
            long offset,
            bool isCompleted,
            string fieldName,
            out byte result,
            out int? minimumBufferLength)
        {
            minimumBufferLength = null;
            if (!reader.TryRead(out var value))
            {
                result = 0;
                if (!isCompleted)
                {
                    minimumBufferLength = GetMinimumBufferLength(reader, offset, 1);
                    return false;
                }

                throw new InvalidOperationException(
                    $"Malformed MessagePack log entry stream at byte offset {offset}: truncated {fieldName}.");
            }

            result = value;
            return true;
        }

        private static bool TryReadUInt16BigEndian(
            ref SequenceReader<byte> reader,
            long offset,
            bool isCompleted,
            string fieldName,
            out ushort result,
            out int? minimumBufferLength)
        {
            result = 0;
            minimumBufferLength = null;
            Span<byte> bytes = stackalloc byte[sizeof(ushort)];
            if (!reader.TryCopyTo(bytes))
            {
                if (!isCompleted)
                {
                    minimumBufferLength = GetMinimumBufferLength(reader, offset, bytes.Length);
                    return false;
                }

                throw new InvalidOperationException(
                    $"Malformed MessagePack log entry stream at byte offset {offset}: truncated {fieldName}.");
            }

            reader.Advance(bytes.Length);
            result = BinaryPrimitives.ReadUInt16BigEndian(bytes);
            return true;
        }

        private static bool TryReadUInt32BigEndian(
            ref SequenceReader<byte> reader,
            long offset,
            bool isCompleted,
            string fieldName,
            out uint result,
            out int? minimumBufferLength)
        {
            result = 0;
            minimumBufferLength = null;
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            if (!reader.TryCopyTo(bytes))
            {
                if (!isCompleted)
                {
                    minimumBufferLength = GetMinimumBufferLength(reader, offset, bytes.Length);
                    return false;
                }

                throw new InvalidOperationException(
                    $"Malformed MessagePack log entry stream at byte offset {offset}: truncated {fieldName}.");
            }

            reader.Advance(bytes.Length);
            result = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            return true;
        }

        private static bool TryReadUInt64BigEndian(
            ref SequenceReader<byte> reader,
            long offset,
            bool isCompleted,
            string fieldName,
            out ulong result,
            out int? minimumBufferLength)
        {
            result = 0;
            minimumBufferLength = null;
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            if (!reader.TryCopyTo(bytes))
            {
                if (!isCompleted)
                {
                    minimumBufferLength = GetMinimumBufferLength(reader, offset, bytes.Length);
                    return false;
                }

                throw new InvalidOperationException(
                    $"Malformed MessagePack log entry stream at byte offset {offset}: truncated {fieldName}.");
            }

            reader.Advance(bytes.Length);
            result = BinaryPrimitives.ReadUInt64BigEndian(bytes);
            return true;
        }

        private static bool TryEnsureRemaining(
            ref SequenceReader<byte> reader,
            uint length,
            long offset,
            bool isCompleted,
            string fieldName,
            out int? minimumBufferLength)
        {
            minimumBufferLength = null;
            if (length > (ulong)reader.Remaining)
            {
                if (!isCompleted)
                {
                    minimumBufferLength = length <= int.MaxValue ? GetMinimumBufferLength(reader, offset, checked((int)length)) : null;
                    return false;
                }

                throw new InvalidOperationException(
                    $"Malformed MessagePack log entry stream at byte offset {offset}: {fieldName} length {length} exceeds remaining input bytes {reader.Remaining}.");
            }

            return true;
        }

        private static int? GetMinimumBufferLength(SequenceReader<byte> reader, long entryOffset, int requiredBytes)
        {
            var consumedInEntry = reader.Consumed - entryOffset;
            var result = consumedInEntry + requiredBytes;
            return result > int.MaxValue ? null : checked((int)result);
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
