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
internal sealed class DurableTaskCompletionSource<T> : IDurableTaskCompletionSource<T>, IDurableStateMachine, IDurableTaskCompletionSourceLogEntryConsumer<T>
{
    private readonly IDurableTaskCompletionSourceCodec<T> _codec;
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
        IStateMachineStorage storage,
        IServiceProvider serviceProvider,
        DeepCopier<T> copier,
        DeepCopier<Exception> exceptionCopier)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = StateMachineLogFormatServices.GetRequiredKeyedService<IDurableTaskCompletionSourceCodecProvider>(serviceProvider, storage).GetCodec<T>();
        _copier = copier;
        _exceptionCopier = exceptionCopier;
        manager.RegisterStateMachine(key, this);
    }

    internal DurableTaskCompletionSource(
        string key,
        IStateMachineManager manager,
        IDurableTaskCompletionSourceCodec<T> codec,
        DeepCopier<T> copier,
        DeepCopier<Exception> exceptionCopier)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
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
        _codec.Apply(logEntry, this);
    }

    void IDurableStateMachine.AppendEntries(StateMachineLogWriter logWriter)
    {
        if (_status is not DurableTaskCompletionSourceStatus.Pending)
        {
            WriteState(logWriter);
        }
    }

    void IDurableStateMachine.AppendSnapshot(StateMachineLogWriter snapshotWriter) => WriteState(snapshotWriter);

    private void WriteState(StateMachineLogWriter writer)
    {
        using var entry = writer.BeginEntry();
        switch (_status)
        {
            case DurableTaskCompletionSourceStatus.Completed:
                _codec.WriteCompleted(_value!, entry.Writer);
                break;
            case DurableTaskCompletionSourceStatus.Faulted:
                _codec.WriteFaulted(_exception!, entry.Writer);
                break;
            case DurableTaskCompletionSourceStatus.Canceled:
                _codec.WriteCanceled(entry.Writer);
                break;
            default:
                _codec.WritePending(entry.Writer);
                break;
        }

        entry.Commit();
    }

    void IDurableTaskCompletionSourceLogEntryConsumer<T>.ApplyPending() => _status = DurableTaskCompletionSourceStatus.Pending;
    void IDurableTaskCompletionSourceLogEntryConsumer<T>.ApplyCompleted(T value)
    {
        _status = DurableTaskCompletionSourceStatus.Completed;
        _value = value;
    }

    void IDurableTaskCompletionSourceLogEntryConsumer<T>.ApplyFaulted(Exception exception)
    {
        _status = DurableTaskCompletionSourceStatus.Faulted;
        _exception = exception;
    }

    void IDurableTaskCompletionSourceLogEntryConsumer<T>.ApplyCanceled() => _status = DurableTaskCompletionSourceStatus.Canceled;

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

