using System.Buffers;
using Google.Protobuf;
using Orleans.Journaling.Protobuf.Messages;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Protocol Buffers <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableValueEntry{T}"/>.
/// </summary>
/// <remarks>
/// Serialized as a <see cref="ValueEntry"/> protobuf message with a <c>oneof command</c> discriminator.
/// User values are embedded as <c>bytes</c> fields serialized via <see cref="ILogDataCodec{T}"/>.
/// </remarks>
public sealed class ProtobufValueEntryCodec<T>(
    ILogDataCodec<T> codec) : ILogEntryCodec<DurableValueEntry<T>>
{
    /// <inheritdoc/>
    public void Write(DurableValueEntry<T> entry, IBufferWriter<byte> output)
    {
        var proto = entry switch
        {
            ValueSetEntry<T>(var value) => new ValueEntry
            {
                Set = new ValueSet { Value = ProtobufCodecHelper.SerializeValue(codec, value) }
            },
            _ => throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}")
        };

        proto.WriteTo(output);
    }

    /// <inheritdoc/>
    public DurableValueEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var proto = ValueEntry.Parser.ParseFrom(input);

        return proto.CommandCase switch
        {
            ValueEntry.CommandOneofCase.Set =>
                new ValueSetEntry<T>(ProtobufCodecHelper.DeserializeValue(codec, proto.Set.Value)),
            _ => throw new NotSupportedException($"Command type {proto.CommandCase} is not supported"),
        };
    }
}

/// <summary>
/// Protocol Buffers <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableStateEntry{T}"/>.
/// </summary>
/// <remarks>
/// Serialized as a <see cref="StateEntry"/> protobuf message with a <c>oneof command</c> discriminator.
/// User values are embedded as <c>bytes</c> fields serialized via <see cref="ILogDataCodec{T}"/>.
/// </remarks>
public sealed class ProtobufStateEntryCodec<T>(
    ILogDataCodec<T> codec) : ILogEntryCodec<DurableStateEntry<T>>
{
    /// <inheritdoc/>
    public void Write(DurableStateEntry<T> entry, IBufferWriter<byte> output)
    {
        var proto = entry switch
        {
            StateSetEntry<T>(var state, var version) => new StateEntry
            {
                Set = new StateSet
                {
                    State = ProtobufCodecHelper.SerializeValue(codec, state),
                    Version = version
                }
            },
            StateClearEntry<T> => new StateEntry { Clear = new StateClear() },
            _ => throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}")
        };

        proto.WriteTo(output);
    }

    /// <inheritdoc/>
    public DurableStateEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var proto = StateEntry.Parser.ParseFrom(input);

        return proto.CommandCase switch
        {
            StateEntry.CommandOneofCase.Set =>
                new StateSetEntry<T>(ProtobufCodecHelper.DeserializeValue(codec, proto.Set.State), proto.Set.Version),
            StateEntry.CommandOneofCase.Clear =>
                new StateClearEntry<T>(),
            _ => throw new NotSupportedException($"Command type {proto.CommandCase} is not supported"),
        };
    }
}

/// <summary>
/// Protocol Buffers <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableTaskCompletionSourceEntry{T}"/>.
/// </summary>
/// <remarks>
/// Serialized as a <see cref="TcsEntry"/> protobuf message with a <c>oneof command</c> discriminator.
/// Exceptions are serialized as their string representation. On deserialization,
/// a new <see cref="Exception"/> is created with the stored message.
/// </remarks>
public sealed class ProtobufTcsEntryCodec<T>(
    ILogDataCodec<T> codec) : ILogEntryCodec<DurableTaskCompletionSourceEntry<T>>
{
    /// <inheritdoc/>
    public void Write(DurableTaskCompletionSourceEntry<T> entry, IBufferWriter<byte> output)
    {
        var proto = entry switch
        {
            TcsCompletedEntry<T>(var value) => new TcsEntry
            {
                Completed = new TcsCompleted { Value = ProtobufCodecHelper.SerializeValue(codec, value) }
            },
            TcsFaultedEntry<T>(var exception) => new TcsEntry
            {
                Faulted = new TcsFaulted { Exception = exception.ToString() }
            },
            TcsCanceledEntry<T> => new TcsEntry { Canceled = new TcsCanceled() },
            TcsPendingEntry<T> => new TcsEntry { Pending = new TcsPending() },
            _ => throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}")
        };

        proto.WriteTo(output);
    }

    /// <inheritdoc/>
    public DurableTaskCompletionSourceEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var proto = TcsEntry.Parser.ParseFrom(input);

        return proto.CommandCase switch
        {
            TcsEntry.CommandOneofCase.Completed =>
                new TcsCompletedEntry<T>(ProtobufCodecHelper.DeserializeValue(codec, proto.Completed.Value)),
            TcsEntry.CommandOneofCase.Faulted =>
                new TcsFaultedEntry<T>(new Exception(proto.Faulted.Exception)),
            TcsEntry.CommandOneofCase.Canceled =>
                new TcsCanceledEntry<T>(),
            TcsEntry.CommandOneofCase.Pending =>
                new TcsPendingEntry<T>(),
            _ => throw new NotSupportedException($"Command type {proto.CommandCase} is not supported"),
        };
    }
}
