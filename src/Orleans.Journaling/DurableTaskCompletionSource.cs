using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace Orleans.Journaling;

/// <summary>
/// Represents a task completion source whose terminal state is recorded in a journal.
/// </summary>
/// <typeparam name="T">The type of the task result.</typeparam>
public interface IDurableTaskCompletionSource<T>
{
    /// <summary>
    /// Gets the task which completes after the terminal state has been persisted.
    /// </summary>
    Task<T> Task { get; }

    /// <summary>
    /// Gets the current durable completion state.
    /// </summary>
    DurableTaskCompletionSourceState<T> State { get; }

    /// <summary>
    /// Attempts to transition the durable state to canceled.
    /// </summary>
    /// <returns><see langword="true"/> if the state was transitioned; otherwise, <see langword="false"/>.</returns>
    bool TrySetCanceled();

    /// <summary>
    /// Attempts to transition the durable state to faulted with the specified exception.
    /// </summary>
    /// <param name="exception">The exception which faults the task.</param>
    /// <returns><see langword="true"/> if the state was transitioned; otherwise, <see langword="false"/>.</returns>
    bool TrySetException(Exception exception);

    /// <summary>
    /// Attempts to transition the durable state to completed with the specified result.
    /// </summary>
    /// <param name="value">The task result.</param>
    /// <returns><see langword="true"/> if the state was transitioned; otherwise, <see langword="false"/>.</returns>
    bool TrySetResult(T value);
}

[DebuggerDisplay("Status = {Status}")]
internal sealed class DurableTaskCompletionSource<T> : IDurableTaskCompletionSource<T>, IStateMachine, IReportsStagedChanges, IDurableTaskCompletionSourceCommandHandler<T>
{
    private readonly IDurableTaskCompletionSourceCommandCodec<T> _codec;
    private readonly DeepCopier<T> _copier;
    private readonly DeepCopier<Exception> _exceptionCopier;
    private readonly JournaledStateAccessTarget _accessTarget;

    private TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DurableTaskCompletionSourceStatus _status;
    private T? _value;
    private Exception? _exception;
    private JournalStreamWriter _writer;

    // Whether the write in flight captured the terminal state. A completed write publishes only the state it captured.
    private bool _isCompletionCaptured;

    public DurableTaskCompletionSource(
        [ServiceKey] string key,
        IJournaledStateManager manager,
        JournaledStateManagerShared shared,
        IServiceProvider serviceProvider,
        DeepCopier<T> copier,
        DeepCopier<Exception> exceptionCopier)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = JournalFormatServices.GetRequiredCommandCodec<IDurableTaskCompletionSourceCommandCodec<T>>(serviceProvider, shared.JournalFormatKey);
        _copier = copier;
        _exceptionCopier = exceptionCopier;
        _accessTarget = new(key, manager);
        manager.RegisterStateMachine(key, this);
    }

    internal DurableTaskCompletionSource(
        string key,
        IJournaledStateManager manager,
        IDurableTaskCompletionSourceCommandCodec<T> codec,
        DeepCopier<T> copier,
        DeepCopier<Exception> exceptionCopier)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        _copier = copier;
        _exceptionCopier = exceptionCopier;
        _accessTarget = new(key, manager);
        manager.RegisterStateMachine(key, this);
    }

    public bool TrySetResult(T value) => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Write(_accessTarget, JournaledStateOperation.CompletionSourceTrySetResult, (Self: this, Value: value), static state => state.Self.TrySetResultCore(state.Value))
        : TrySetResultCore(value);

    public bool TrySetException(Exception exception) => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Write(_accessTarget, JournaledStateOperation.CompletionSourceTrySetException, (Self: this, Exception: exception), static state => state.Self.TrySetExceptionCore(state.Exception))
        : TrySetExceptionCore(exception);

    public bool TrySetCanceled() => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Write(_accessTarget, JournaledStateOperation.CompletionSourceTrySetCanceled, this, static self => self.TrySetCanceledCore())
        : TrySetCanceledCore();

    private bool TrySetResultCore(T value)
    {
        if (_status is not DurableTaskCompletionSourceStatus.Pending)
        {
            return false;
        }

        _status = DurableTaskCompletionSourceStatus.Completed;
        _writer.MarkModified();
        _value = _copier.Copy(value);
        return true;
    }

    private bool TrySetExceptionCore(Exception exception)
    {
        if (_status is not DurableTaskCompletionSourceStatus.Pending)
        {
            return false;
        }

        _status = DurableTaskCompletionSourceStatus.Faulted;
        _writer.MarkModified();
        _exception = _exceptionCopier.Copy(exception);
        return true;
    }

    private bool TrySetCanceledCore()
    {
        if (_status is not DurableTaskCompletionSourceStatus.Pending)
        {
            return false;
        }

        _status = DurableTaskCompletionSourceStatus.Canceled;
        _writer.MarkModified();
        return true;
    }

    public Task<T> Task => _completion.Task;

    public DurableTaskCompletionSourceState<T> State => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Read(_accessTarget, JournaledStateOperation.CompletionSourceState, this, static self => self.StateCore)
        : StateCore;

    private DurableTaskCompletionSourceState<T> StateCore => _status switch
    {
        DurableTaskCompletionSourceStatus.Pending => new DurableTaskCompletionSourceState<T> { Status = DurableTaskCompletionSourceStatus.Pending },
        DurableTaskCompletionSourceStatus.Completed => new DurableTaskCompletionSourceState<T> { Status = DurableTaskCompletionSourceStatus.Completed, Value = _value },
        DurableTaskCompletionSourceStatus.Faulted => new DurableTaskCompletionSourceState<T> { Status = DurableTaskCompletionSourceStatus.Faulted, Exception = _exception },
        DurableTaskCompletionSourceStatus.Canceled => new DurableTaskCompletionSourceState<T> { Status = DurableTaskCompletionSourceStatus.Canceled },
        _ => throw new InvalidOperationException($"Unexpected status, \"{_status}\""),
    };

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

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

    void IStateMachine.OnRecoveryCompleted() => OnValuePersisted();

    void IStateMachine.OnWriteCompleted()
    {
        if (_isCompletionCaptured)
        {
            _isCompletionCaptured = false;
            OnValuePersisted();
        }
    }

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        _writer = writer;
        _status = DurableTaskCompletionSourceStatus.Pending;
        _value = default;
        _exception = null;
        _isCompletionCaptured = false;

        // Reset the task completion source if necessary.
        if (_completion.Task.IsCompleted)
        {
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer)
    {
        if (_status is not DurableTaskCompletionSourceStatus.Pending)
        {
            WriteState(writer);
            _isCompletionCaptured = true;
        }
    }

    void IStateMachine.WriteSnapshot(JournalStreamWriter snapshotWriter)
    {
        WriteState(snapshotWriter);
        _isCompletionCaptured = _status is not DurableTaskCompletionSourceStatus.Pending;
    }

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

    void IDurableTaskCompletionSourceCommandHandler<T>.ApplyPending() => _status = DurableTaskCompletionSourceStatus.Pending;
    void IDurableTaskCompletionSourceCommandHandler<T>.ApplyCompleted(T value)
    {
        _status = DurableTaskCompletionSourceStatus.Completed;
        _writer.MarkModified();
        _value = value;
    }

    void IDurableTaskCompletionSourceCommandHandler<T>.ApplyFaulted(Exception exception)
    {
        _status = DurableTaskCompletionSourceStatus.Faulted;
        _writer.MarkModified();
        _exception = exception;
    }

    void IDurableTaskCompletionSourceCommandHandler<T>.ApplyCanceled() => _status = DurableTaskCompletionSourceStatus.Canceled;

}

/// <summary>
/// Identifies the state of a durable task completion source.
/// </summary>
[GenerateSerializer]
public enum DurableTaskCompletionSourceStatus : byte
{
    /// <summary>
    /// The task is awaiting completion.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// The task completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// The task completed with an exception.
    /// </summary>
    Faulted,

    /// <summary>
    /// The task was canceled.
    /// </summary>
    Canceled
}

/// <summary>
/// Describes the state of a durable task completion source.
/// </summary>
/// <typeparam name="T">The type of the task result.</typeparam>
[GenerateSerializer, Immutable]
public readonly struct DurableTaskCompletionSourceState<T>
{
    /// <summary>
    /// Gets the completion status.
    /// </summary>
    [Id(0)]
    public DurableTaskCompletionSourceStatus Status { get; init; }

    /// <summary>
    /// Gets the result for a successfully completed task.
    /// </summary>
    [Id(1)]
    public T? Value { get; init; }

    /// <summary>
    /// Gets the exception for a faulted task.
    /// </summary>
    [Id(2)]
    public Exception? Exception { get; init; }
}
