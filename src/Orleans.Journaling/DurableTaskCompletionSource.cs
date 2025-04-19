using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

public interface IDurableTaskCompletionSource<T>
{
    Task<T> Task { get; }
    DurableTaskCompletionSourceState<T> State { get; }

    bool TrySetCanceled();
    bool TrySetException(Exception exception);
    bool TrySetResult(T value);
}

[DebuggerDisplay("Status = {Status}")]
internal sealed class DurableTaskCompletionSource<T> : IDurableTaskCompletionSource<T>, IDurableStateMachine
{
    private const byte SupportedVersion = 0;
    private readonly SerializerSessionPool _serializerSessionPool;
    private readonly IFieldCodec<T> _codec;
    private readonly IFieldCodec<Exception> _exceptionCodec;
    private readonly DeepCopier<T> _copier;
    private readonly DeepCopier<Exception> _exceptionCopier;

    private TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IStateMachineLogWriter? _storage;
    private DurableTaskCompletionSourceStatus _status;
    private T? _value;
    private Exception? _exception;

    public DurableTaskCompletionSource(
        [ServiceKey] string key,
        IStateMachineManager manager,
        IFieldCodec<T> codec,
        DeepCopier<T> copier,
        IFieldCodec<Exception> exceptionCodec,
        DeepCopier<Exception> exceptionCopier,
        SerializerSessionPool serializerSessionPool)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        _copier = copier;
        _exceptionCodec = exceptionCodec;
        _exceptionCopier = exceptionCopier;
        _serializerSessionPool = serializerSessionPool;
        manager.RegisterStateMachine(key, this);
    }

    public bool TrySetResult(T value)
    {
        if (_status is not DurableTaskCompletionSourceStatus.Pending)
        {
            return false;
        }

        _status = DurableTaskCompletionSourceStatus.Completed;
        _value = _copier.Copy(value);
        return true;
    }

    public bool TrySetException(Exception exception)
    {
        if (_status is not DurableTaskCompletionSourceStatus.Pending)
        {
            return false;
        }

        _status = DurableTaskCompletionSourceStatus.Faulted;
        _exception = _exceptionCopier.Copy(exception);
        return true;
    }

    public bool TrySetCanceled()
    {
        if (_status is not DurableTaskCompletionSourceStatus.Pending)
        {
            return false;
        }

        _status = DurableTaskCompletionSourceStatus.Canceled;
        return true;
    }

    public Task<T> Task => _completion.Task;

    public DurableTaskCompletionSourceState<T> State => _status switch
    {
        DurableTaskCompletionSourceStatus.Pending => new DurableTaskCompletionSourceState<T> { Status = DurableTaskCompletionSourceStatus.Pending },
        DurableTaskCompletionSourceStatus.Completed => new DurableTaskCompletionSourceState<T> { Status = DurableTaskCompletionSourceStatus.Completed, Value = _value },
        DurableTaskCompletionSourceStatus.Faulted => new DurableTaskCompletionSourceState<T> { Status = DurableTaskCompletionSourceStatus.Faulted, Exception = _exception },
        DurableTaskCompletionSourceStatus.Canceled => new DurableTaskCompletionSourceState<T> { Status = DurableTaskCompletionSourceStatus.Canceled },
        _ => throw new InvalidOperationException($"Unexpected status, \"{_status}\""),
    };

    private void OnValuePersisted()
    {
        switch (_status)
        {
            case DurableTaskCompletionSourceStatus.Completed:
                _completion.TrySetResult(_value!);
                break;
            case DurableTaskCompletionSourceStatus.Faulted:
                _completion.TrySetException(_exception!);
                break;
            case DurableTaskCompletionSourceStatus.Canceled:
                _completion.TrySetCanceled();
                break;
            default:
                break;
        }
    }

    void IDurableStateMachine.OnRecoveryCompleted() => OnValuePersisted();
    void IDurableStateMachine.OnWriteCompleted() => OnValuePersisted();

    void IDurableStateMachine.Reset(IStateMachineLogWriter storage)
    {
        // Reset the task completion source if necessary.
        if (_completion.Task.IsCompleted)
        {
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _storage = storage;
    }

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry)
    {
        using var session = _serializerSessionPool.GetSession();
        var reader = Reader.Create(logEntry, session);
        var version = reader.ReadByte();
        if (version != SupportedVersion)
        {
            throw new NotSupportedException($"This instance of {nameof(DurableTaskCompletionSource<T>)} supports version {(uint)SupportedVersion} and not version {(uint)version}.");
        }

        _status = (DurableTaskCompletionSourceStatus)reader.ReadVarUInt32();
        switch (_status)
        {
            case DurableTaskCompletionSourceStatus.Completed:
                _value = ReadValue(ref reader);
                break;
            case DurableTaskCompletionSourceStatus.Faulted:
                _exception = ReadException(ref reader);
                break;
            default:
                break;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        T ReadValue(ref Reader<ReadOnlySequenceInput> reader)
        {
            var field = reader.ReadFieldHeader();
            return _codec.ReadValue(ref reader, field);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        Exception ReadException(ref Reader<ReadOnlySequenceInput> reader)
        {
            var field = reader.ReadFieldHeader();
            return _exceptionCodec.ReadValue(ref reader, field);
        }
    }

    void IDurableStateMachine.AppendEntries(StateMachineStorageWriter logWriter)
    {
        if (_status is not DurableTaskCompletionSourceStatus.Pending)
        {
            WriteState(logWriter);
        }
    }

    void IDurableStateMachine.AppendSnapshot(StateMachineStorageWriter snapshotWriter) => WriteState(snapshotWriter);

    private void WriteState(StateMachineStorageWriter writer)
    {
        writer.AppendEntry(static (self, bufferWriter) =>
        {
            using var session = self._serializerSessionPool.GetSession();
            var writer = Writer.Create(bufferWriter, session);
            writer.WriteByte(DurableTaskCompletionSource<T>.SupportedVersion);
            var status = self._status;
            writer.WriteByte((byte)status);
            if (status is DurableTaskCompletionSourceStatus.Completed)
            {
                self._codec.WriteField(ref writer, 0, typeof(T), self._value!);
            }
            else if (status is DurableTaskCompletionSourceStatus.Faulted)
            {
                self._exceptionCodec.WriteField(ref writer, 0, typeof(Exception), self._exception!);
            }

            writer.Commit();
        }, this);
    }

    public IDurableStateMachine DeepCopy() => throw new NotImplementedException();
}

[GenerateSerializer]
public enum DurableTaskCompletionSourceStatus : byte
{
    Pending = 0,
    Completed,
    Faulted,
    Canceled
}

[GenerateSerializer, Immutable]
public readonly struct DurableTaskCompletionSourceState<T>
{
    [Id(0)]
    public DurableTaskCompletionSourceStatus Status { get; init; }

    [Id(1)]
    public T? Value { get; init; }

    [Id(2)]
    public Exception? Exception { get; init; }
}

