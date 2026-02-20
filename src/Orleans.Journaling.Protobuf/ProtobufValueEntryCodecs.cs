using System.Buffers;
using Google.Protobuf;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Protocol Buffers <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableValueEntry{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each entry is encoded as a protobuf message using the following field layout:
/// </para>
/// <list type="bullet">
/// <item><description>Field 1 (uint32): command (0 = set)</description></item>
/// <item><description>Field 2 (bytes): value</description></item>
/// </list>
/// </remarks>
public sealed class ProtobufValueEntryCodec<T>(
    ILogDataCodec<T> codec) : ILogEntryCodec<DurableValueEntry<T>>
{
    private const uint SetCommand = 0;

    private const uint TagCommand = (1 << 3) | 0;   // Field 1, varint
    private const uint TagValue = (2 << 3) | 2;     // Field 2, length-delimited

    /// <inheritdoc/>
    public void Write(DurableValueEntry<T> entry, IBufferWriter<byte> output)
    {
        var stream = new MemoryStream();
        var cos = new CodedOutputStream(stream, leaveOpen: true);

        switch (entry)
        {
            case ValueSetEntry<T>(var value):
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(SetCommand);
                cos.WriteTag(TagValue);
                cos.WriteBytes(ProtobufCodecHelper.SerializeValue(codec, value));
                break;
            default:
                throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}");
        }

        cos.Flush();
        ProtobufCodecHelper.CopyToBufferWriter(stream, output);
    }

    /// <inheritdoc/>
    public DurableValueEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var cis = new CodedInputStream(input.ToArray());

        uint command = 0;
        ByteString? valueBytes = null;

        while (!cis.IsAtEnd)
        {
            var tag = cis.ReadTag();
            switch (tag)
            {
                case TagCommand:
                    command = cis.ReadUInt32();
                    break;
                case TagValue:
                    valueBytes = cis.ReadBytes();
                    break;
                default:
                    ProtobufCodecHelper.SkipField(cis, WireFormat.GetTagWireType(tag));
                    break;
            }
        }

        return command switch
        {
            SetCommand => new ValueSetEntry<T>(ProtobufCodecHelper.DeserializeValue(codec, valueBytes!)),
            _ => throw new NotSupportedException($"Command type {command} is not supported"),
        };
    }
}

/// <summary>
/// Protocol Buffers <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableStateEntry{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each entry is encoded as a protobuf message using the following field layout:
/// </para>
/// <list type="bullet">
/// <item><description>Field 1 (uint32): command (0 = set, 1 = clear)</description></item>
/// <item><description>Field 2 (bytes): state value</description></item>
/// <item><description>Field 3 (uint64): version</description></item>
/// </list>
/// </remarks>
public sealed class ProtobufStateEntryCodec<T>(
    ILogDataCodec<T> codec) : ILogEntryCodec<DurableStateEntry<T>>
{
    private const uint SetCommand = 0;
    private const uint ClearCommand = 1;

    private const uint TagCommand = (1 << 3) | 0;   // Field 1, varint
    private const uint TagState = (2 << 3) | 2;     // Field 2, length-delimited
    private const uint TagVersion = (3 << 3) | 0;   // Field 3, varint

    /// <inheritdoc/>
    public void Write(DurableStateEntry<T> entry, IBufferWriter<byte> output)
    {
        var stream = new MemoryStream();
        var cos = new CodedOutputStream(stream, leaveOpen: true);

        switch (entry)
        {
            case StateSetEntry<T>(var state, var version):
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(SetCommand);
                cos.WriteTag(TagState);
                cos.WriteBytes(ProtobufCodecHelper.SerializeValue(codec, state));
                cos.WriteTag(TagVersion);
                cos.WriteUInt64(version);
                break;
            case StateClearEntry<T>:
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(ClearCommand);
                break;
            default:
                throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}");
        }

        cos.Flush();
        ProtobufCodecHelper.CopyToBufferWriter(stream, output);
    }

    /// <inheritdoc/>
    public DurableStateEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var cis = new CodedInputStream(input.ToArray());

        uint command = 0;
        ByteString? stateBytes = null;
        ulong version = 0;

        while (!cis.IsAtEnd)
        {
            var tag = cis.ReadTag();
            switch (tag)
            {
                case TagCommand:
                    command = cis.ReadUInt32();
                    break;
                case TagState:
                    stateBytes = cis.ReadBytes();
                    break;
                case TagVersion:
                    version = cis.ReadUInt64();
                    break;
                default:
                    ProtobufCodecHelper.SkipField(cis, WireFormat.GetTagWireType(tag));
                    break;
            }
        }

        return command switch
        {
            SetCommand => new StateSetEntry<T>(ProtobufCodecHelper.DeserializeValue(codec, stateBytes!), version),
            ClearCommand => new StateClearEntry<T>(),
            _ => throw new NotSupportedException($"Command type {command} is not supported"),
        };
    }
}

/// <summary>
/// Protocol Buffers <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableTaskCompletionSourceEntry{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each entry is encoded as a protobuf message using the following field layout:
/// </para>
/// <list type="bullet">
/// <item><description>Field 1 (uint32): command (0 = completed, 1 = faulted, 2 = canceled, 3 = pending)</description></item>
/// <item><description>Field 2 (bytes): value (for completed)</description></item>
/// <item><description>Field 3 (string): exception message (for faulted)</description></item>
/// </list>
/// <para>
/// Exceptions are serialized as their string representation. On deserialization,
/// a new <see cref="Exception"/> is created with the stored message.
/// </para>
/// </remarks>
public sealed class ProtobufTcsEntryCodec<T>(
    ILogDataCodec<T> codec) : ILogEntryCodec<DurableTaskCompletionSourceEntry<T>>
{
    private const uint CompletedCommand = 0;
    private const uint FaultedCommand = 1;
    private const uint CanceledCommand = 2;
    private const uint PendingCommand = 3;

    private const uint TagCommand = (1 << 3) | 0;       // Field 1, varint
    private const uint TagValue = (2 << 3) | 2;         // Field 2, length-delimited
    private const uint TagException = (3 << 3) | 2;     // Field 3, length-delimited

    /// <inheritdoc/>
    public void Write(DurableTaskCompletionSourceEntry<T> entry, IBufferWriter<byte> output)
    {
        var stream = new MemoryStream();
        var cos = new CodedOutputStream(stream, leaveOpen: true);

        switch (entry)
        {
            case TcsCompletedEntry<T>(var value):
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(CompletedCommand);
                cos.WriteTag(TagValue);
                cos.WriteBytes(ProtobufCodecHelper.SerializeValue(codec, value));
                break;
            case TcsFaultedEntry<T>(var exception):
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(FaultedCommand);
                cos.WriteTag(TagException);
                cos.WriteString(exception.ToString());
                break;
            case TcsCanceledEntry<T>:
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(CanceledCommand);
                break;
            case TcsPendingEntry<T>:
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(PendingCommand);
                break;
            default:
                throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}");
        }

        cos.Flush();
        ProtobufCodecHelper.CopyToBufferWriter(stream, output);
    }

    /// <inheritdoc/>
    public DurableTaskCompletionSourceEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var cis = new CodedInputStream(input.ToArray());

        uint command = 0;
        ByteString? valueBytes = null;
        string? exceptionMessage = null;

        while (!cis.IsAtEnd)
        {
            var tag = cis.ReadTag();
            switch (tag)
            {
                case TagCommand:
                    command = cis.ReadUInt32();
                    break;
                case TagValue:
                    valueBytes = cis.ReadBytes();
                    break;
                case TagException:
                    exceptionMessage = cis.ReadString();
                    break;
                default:
                    ProtobufCodecHelper.SkipField(cis, WireFormat.GetTagWireType(tag));
                    break;
            }
        }

        return command switch
        {
            CompletedCommand => new TcsCompletedEntry<T>(ProtobufCodecHelper.DeserializeValue(codec, valueBytes!)),
            FaultedCommand => new TcsFaultedEntry<T>(new Exception(exceptionMessage)),
            CanceledCommand => new TcsCanceledEntry<T>(),
            PendingCommand => new TcsPendingEntry<T>(),
            _ => throw new NotSupportedException($"Command type {command} is not supported"),
        };
    }
}
