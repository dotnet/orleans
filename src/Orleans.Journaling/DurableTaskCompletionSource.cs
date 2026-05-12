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
internal sealed class DurableTaskCompletionSource<T> : IDurableTaskCompletionSource<T>, IJournaledState, IDurableTaskCompletionSourceOperationHandler<T>
{
    private readonly IDurableTaskCompletionSourceOperationCodec<T> _codec;
    private readonly DeepCopier<T> _copier;
    private readonly DeepCopier<Exception> _exceptionCopier;

    private TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DurableTaskCompletionSourceStatus _status;
    private T? _value;
    private Exception? _exception;

    public DurableTaskCompletionSource(
        [ServiceKey] string key,
        IStateManager manager,
        JournaledStateManagerShared shared,
        IServiceProvider serviceProvider,
        DeepCopier<T> copier,
        DeepCopier<Exception> exceptionCopier)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = JournalFormatServices.GetRequiredOperationCodec<IDurableTaskCompletionSourceOperationCodec<T>>(serviceProvider, shared.JournalFormatKey);
        _copier = copier;
        _exceptionCopier = exceptionCopier;
        manager.RegisterState(key, this);
    }

    internal DurableTaskCompletionSource(
        string key,
        IStateManager manager,
        IDurableTaskCompletionSourceOperationCodec<T> codec,
        DeepCopier<T> copier,
        DeepCopier<Exception> exceptionCopier)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        _copier = copier;
        _exceptionCopier = exceptionCopier;
        manager.RegisterState(key, this);
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

    object IJournaledState.OperationCodec => _codec;

    Type IJournaledState.OperationCodecServiceType => typeof(IDurableTaskCompletionSourceOperationCodec<T>);

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

    void IJournaledState.OnRecoveryCompleted() => OnValuePersisted();
    void IJournaledState.OnWriteCompleted() => OnValuePersisted();

    void IJournaledState.Reset(JournalStreamWriter writer)
    {
        _status = DurableTaskCompletionSourceStatus.Pending;
        _value = default;
        _exception = null;

        // Reset the task completion source if necessary.
        if (_completion.Task.IsCompleted)
        {
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    void IJournaledState.AppendEntries(JournalStreamWriter writer)
    {
        if (_status is not DurableTaskCompletionSourceStatus.Pending)
        {
            WriteState(writer);
        }
    }

    void IJournaledState.AppendSnapshot(JournalStreamWriter snapshotWriter) => WriteState(snapshotWriter);

    private void WriteState(JournalStreamWriter writer)
    {
        switch (_status)
        {
            case DurableTaskCompletionSourceStatus.Completed:
                _codec.WriteCompleted(_value!, writer);
                break;
            case DurableTaskCompletionSourceStatus.Faulted:
                _codec.WriteFaulted(_exception!, writer);
                break;
            case DurableTaskCompletionSourceStatus.Canceled:
                _codec.WriteCanceled(writer);
                break;
            default:
                _codec.WritePending(writer);
                break;
        }
    }

    void IDurableTaskCompletionSourceOperationHandler<T>.ApplyPending() => _status = DurableTaskCompletionSourceStatus.Pending;
    void IDurableTaskCompletionSourceOperationHandler<T>.ApplyCompleted(T value)
    {
        _status = DurableTaskCompletionSourceStatus.Completed;
        _value = value;
    }

    void IDurableTaskCompletionSourceOperationHandler<T>.ApplyFaulted(Exception exception)
    {
        _status = DurableTaskCompletionSourceStatus.Faulted;
        _exception = exception;
    }

    void IDurableTaskCompletionSourceOperationHandler<T>.ApplyCanceled() => _status = DurableTaskCompletionSourceStatus.Canceled;

    public IJournaledState DeepCopy() => throw new NotImplementedException();
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

