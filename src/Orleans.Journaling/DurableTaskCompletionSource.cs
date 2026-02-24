using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

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
    private readonly ILogEntryCodec<DurableTaskCompletionSourceEntry<T>> _entryCodec;
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
        IDurableTaskCompletionSourceCodecProvider codecProvider,
        DeepCopier<T> copier,
        DeepCopier<Exception> exceptionCopier)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _entryCodec = codecProvider.GetCodec<T>();
        _copier = copier;
        _exceptionCopier = exceptionCopier;
        manager.RegisterStateMachine(key, this);
    }

    internal DurableTaskCompletionSource(
        string key,
        IStateMachineManager manager,
        ILogEntryCodec<DurableTaskCompletionSourceEntry<T>> entryCodec,
        DeepCopier<T> copier,
        DeepCopier<Exception> exceptionCopier)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _entryCodec = entryCodec;
        _copier = copier;
        _exceptionCopier = exceptionCopier;
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
        var entry = _entryCodec.Read(logEntry);
        switch (entry)
        {
            case TcsCompletedEntry<T>(var value):
                _status = DurableTaskCompletionSourceStatus.Completed;
                _value = value;
                break;
            case TcsFaultedEntry<T>(var exception):
                _status = DurableTaskCompletionSourceStatus.Faulted;
                _exception = exception;
                break;
            case TcsCanceledEntry<T>:
                _status = DurableTaskCompletionSourceStatus.Canceled;
                break;
            case TcsPendingEntry<T>:
                _status = DurableTaskCompletionSourceStatus.Pending;
                break;
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
            DurableTaskCompletionSourceEntry<T> entry = self._status switch
            {
                DurableTaskCompletionSourceStatus.Completed => new TcsCompletedEntry<T>(self._value!),
                DurableTaskCompletionSourceStatus.Faulted => new TcsFaultedEntry<T>(self._exception!),
                DurableTaskCompletionSourceStatus.Canceled => new TcsCanceledEntry<T>(),
                _ => new TcsPendingEntry<T>(),
            };
            self._entryCodec.Write(entry, bufferWriter);
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

