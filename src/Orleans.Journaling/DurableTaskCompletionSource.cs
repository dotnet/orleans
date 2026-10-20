using System.Buffers;
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
internal sealed class DurableTaskCompletionSource<T> : IDurableTaskCompletionSource<T>, IStateMachine, IReportsStagedChanges, IDurableTaskCompletionSourceCommandHandler<T>, IReplicableState, IJournaledStateReplica, ICoalescingJournaledState
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
        _accessTarget = new(key, manager, this);
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
        _accessTarget = new(key, manager, this);
        manager.RegisterStateMachine(key, this);
    }

    public bool TrySetResult(T value) => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Write(_accessTarget, JournaledStateOperation.CompletionSourceTrySetResult, value, (Self: this, Value: value), static state => state.Self.TrySetResultCore(state.Value))
        : TrySetResultCore(value);

    public bool TrySetException(Exception exception) => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Write(_accessTarget, JournaledStateOperation.CompletionSourceTrySetException, exception.GetType().FullName, (Self: this, Exception: exception), static state => state.Self.TrySetExceptionCore(state.Exception))
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

        ApplyCompleted(_copier.Copy(value));
        return true;
    }

    private bool TrySetExceptionCore(Exception exception)
    {
        if (_status is not DurableTaskCompletionSourceStatus.Pending)
        {
            return false;
        }

        ApplyFaulted(_exceptionCopier.Copy(exception));
        return true;
    }

    private bool TrySetCanceledCore()
    {
        if (_status is not DurableTaskCompletionSourceStatus.Pending)
        {
            return false;
        }

        ApplyCanceled();
        _writer.MarkModified();
        return true;
    }

    public Task<T> Task => _completion.Task;

    public DurableTaskCompletionSourceState<T> State => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.ReadWhole(_accessTarget, JournaledStateOperation.CompletionSourceState, this, static self => self.StateCore)
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
        ApplyPending();
        _isCompletionCaptured = false;

        // Reset the task completion source if necessary.
        if (_completion.Task.IsCompleted)
        {
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer)
    {
        Debug.Assert(_accessTarget.Manager is not { } manager || !manager.IsHeldByTransaction(this), "A write captures no state which an open transaction holds.");
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

    // A transaction journals the completion through its stream: into the journal when it first writes the completion
    // source, and into its segment when it commits. The write which captures this state after the transaction ends journals
    // the completion again and publishes it.
    void ICoalescingJournaledState.StagePendingChanges(JournalStreamWriter writer)
    {
        if (_status is not DurableTaskCompletionSourceStatus.Pending)
        {
            WriteState(writer);
        }
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

    void IDurableTaskCompletionSourceCommandHandler<T>.ApplyPending() => ApplyPending();
    void IDurableTaskCompletionSourceCommandHandler<T>.ApplyCompleted(T value) => ApplyCompleted(value);
    void IDurableTaskCompletionSourceCommandHandler<T>.ApplyFaulted(Exception exception) => ApplyFaulted(exception);
    void IDurableTaskCompletionSourceCommandHandler<T>.ApplyCanceled() => ApplyCanceled();

    // The Apply methods make every change of the state which reads observe, for live operations and replayed entries
    // alike, so they record each change.
    private void ApplyPending()
    {
        _accessTarget.OnChanged();
        _status = DurableTaskCompletionSourceStatus.Pending;
        _value = default;
        _exception = null;
    }

    private void ApplyCompleted(T? value)
    {
        _accessTarget.OnChanged();
        _status = DurableTaskCompletionSourceStatus.Completed;
        _writer.MarkModified();
        _value = value;
    }

    private void ApplyFaulted(Exception exception)
    {
        _accessTarget.OnChanged();
        _status = DurableTaskCompletionSourceStatus.Faulted;
        _writer.MarkModified();
        _exception = exception;
    }

    private void ApplyCanceled()
    {
        _accessTarget.OnChanged();
        _status = DurableTaskCompletionSourceStatus.Canceled;
    }

    // A replica, which has no manager, so its reads run directly.
    private DurableTaskCompletionSource(
        IDurableTaskCompletionSourceCommandCodec<T> codec,
        DeepCopier<T> copier,
        DeepCopier<Exception> exceptionCopier)
    {
        _codec = codec;
        _copier = copier;
        _exceptionCopier = exceptionCopier;
        _accessTarget = new(nameof(DurableTaskCompletionSource<T>));
    }

    // A completion journals its state at the next write. Until a write captures it, or acknowledges it, which completes the
    // task, the journal doesn't hold it. A fault's journaled exception doesn't reproduce the exception which reads observe
    // (its type, stack trace and data can differ), so reads of a faulted source record values.
    bool IReplicableState.IsReproducibleFromJournal => _status switch
    {
        DurableTaskCompletionSourceStatus.Pending => true,
        DurableTaskCompletionSourceStatus.Faulted => false,
        _ => _isCompletionCaptured || _completion.Task.IsCompleted,
    };

    IJournaledStateReplica IReplicableState.CreateReplica() => new DurableTaskCompletionSource<T>(_codec, _copier, _exceptionCopier);

    void IJournaledStateReplica.Evaluate(JournaledStateOperation operation, ReadOnlyMemory<byte> argument, Serializer serializer, IBufferWriter<byte> result)
    {
        if (operation is not JournaledStateOperation.CompletionSourceState)
        {
            throw JournaledStateReplicaEncoding.NotPositional(operation, nameof(DurableTaskCompletionSource<T>));
        }

        JournaledStateReplicaEncoding.WriteResult(serializer, StateCore, result);
    }
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
