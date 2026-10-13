using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.DurableTasks.Runtime;

/// <remarks>
/// <para>
/// A signal addresses an invoked task, which records the first value for each signal name as a terminal record in its
/// execution tree: the child <c>$signal:{name}</c>, whose fingerprint is the value's fingerprint and whose result is the
/// value. The record commits with the change which delivered it and is deleted with the tree, so every wait in the tree,
/// including waits during replay, observes the same value. A signal with an equal value is a duplicate, and a signal
/// with a different value, or for a task which this grain doesn't retain as invoked or which is terminal without that
/// record, is rejected.
/// </para>
/// <para>
/// Waits complete once the record commits. A wait which is abandoned, for example because durable cancellation or tree
/// deletion canceled its execution, removes itself, so the runtime retains no state for signals which never arrive.
/// </para>
/// </remarks>
internal sealed partial class DurableTaskGrainRuntime
{
    // Execution-tree records begin with '$', so a signal record never collides with an explicitly named child.
    private const string SignalSegmentPrefix = "$signal:";
    private const string SignalFingerprintPrefix = "$signal:";
    private const string SignalSendFingerprintPrefix = "$signal-send:";

    // The waits for signals which haven't committed, by signal record.
    private readonly Dictionary<TaskId, List<TaskCompletionSource>> _signalWaits = [];

    public async ValueTask<(DurableTaskResponse Value, DateTimeOffset RecordedAt)> WaitForSignalAsync(
        TaskId executionId,
        string name,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        var recordId = GetSignalRecordId(GetSignalOwner(executionId), name);
        while (true)
        {
            // A staged record commits before its waits observe it, like other results.
            if (_storage.TryGetTask(recordId, out var record) && !_pendingHandleResponses.ContainsKey(recordId))
            {
                Debug.Assert(record.CompletedAt.HasValue, "A signal record holds its value.");
                return (record.Result!, record.CompletedAt.Value);
            }

            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            List<TaskCompletionSource>? waiters;
            lock (_signalWaits)
            {
                if (!_signalWaits.TryGetValue(recordId, out waiters))
                {
                    _signalWaits[recordId] = waiters = [];
                }

                waiters.Add(waiter);
            }

            try
            {
                await waiter.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                lock (_signalWaits)
                {
                    if (waiters.Remove(waiter) && waiters.Count == 0)
                    {
                        _signalWaits.Remove(recordId);
                    }
                }
            }
        }
    }

    /// <summary>Completes the waits for a signal record whose value has committed.</summary>
    private void CompleteSignalWaits(TaskId recordId)
    {
        List<TaskCompletionSource>? waiters;
        lock (_signalWaits)
        {
            if (_signalWaits.Count == 0 || !_signalWaits.Remove(recordId, out waiters))
            {
                return;
            }
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult();
        }
    }

    /// <summary>
    /// Sends a signal from a durable execution, recording the send under <paramref name="markerId"/> in the execution's
    /// tree, and waits for the journal write which commits both.
    /// </summary>
    /// <remarks>
    /// A signal for this grain is staged directly in task state; one for another grain is staged in the outbox. A replay
    /// which reaches the marker sends nothing again, and the marker is deleted with the execution's tree.
    /// </remarks>
    public async ValueTask SendSignalAsync(
        TaskId markerId,
        GrainId target,
        TaskId taskId,
        string name,
        DurableTaskResponse value,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSignal(taskId, name, value);
        var payloadFingerprint = GetSignalFingerprint(value);
        var markerFingerprint = GetSignalSendFingerprint(target, taskId, name, payloadFingerprint);
        if (_storage.TryGetTask(markerId, out var recorded))
        {
            if (!string.Equals(recorded.RequestFingerprint, markerFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Durable task '{markerId}' is already associated with another operation.");
            }

            // The marker commits with the send, possibly in a write which an earlier attempt is still awaiting.
            await WriteStateAsync(cancellationToken);
            return;
        }

        if (target == GrainId)
        {
            if (StageSignal(taskId, name, value, payloadFingerprint) is { } rejection)
            {
                throw rejection;
            }
        }
        else
        {
            MessageTransport.SendSignal(GrainId, target, taskId, name, value, payloadFingerprint);
        }

        var marker = _storage.GetOrCreateTask(markerId, request: null);
        _storage.SetRequestFingerprint(markerId, marker, markerFingerprint);
        _storage.SetResponse(markerId, marker, DurableTaskResponse.Completed);
        await WriteStateAsync(cancellationToken);
    }

    /// <summary>Stages a signal received through the inbox, completing its waits after the inbox batch commits.</summary>
    /// <returns><see langword="null"/> when the signal is accepted, or the reason it is rejected.</returns>
    internal Exception? StageSignalFromInbox(TaskId taskId, string name, DurableTaskResponse value)
    {
        ThrowIfStopping();
        ValidateSignal(taskId, name, value);
        return StageSignal(taskId, name, value, GetSignalFingerprint(value));
    }

    ValueTask IDurableTaskGrainExtension.SignalAsync(
        TaskId taskId,
        string name,
        DurableTaskResponse value,
        CancellationToken cancellationToken) =>
        SignalOwnTaskAsync(taskId, name, value, cancellationToken);

    ValueTask IDurableTaskManager.SignalAsync<T>(
        TaskId taskId,
        DurableSignal<T> signal,
        T value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var response = DurableTaskResponse.FromResult(value);

        // A durable execution records the send, so its replay doesn't depend on the target's current state.
        return DurableExecutionContext.Current is GrainDurableExecutionContext context
            ? context.SendSignalAsync(GrainId, taskId, signal.Name, response, cancellationToken)
            : SignalOwnTaskAsync(taskId, signal.Name, response, cancellationToken);
    }

    /// <summary>Stages a signal for a task of this grain and waits for the journal write which commits it.</summary>
    /// <remarks>A duplicate also waits for that write, so the value is durable when the operation completes.</remarks>
    private async ValueTask SignalOwnTaskAsync(TaskId taskId, string name, DurableTaskResponse value, CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        ValidateSignal(taskId, name, value);
        if (StageSignal(taskId, name, value, GetSignalFingerprint(value)) is { } rejection)
        {
            throw rejection;
        }

        await WriteStateAsync(cancellationToken);
    }

    /// <summary>Records the first value of a signal for an invoked task, without writing.</summary>
    /// <returns><see langword="null"/> when the signal is recorded or is a duplicate, or the reason it is rejected.</returns>
    private Exception? StageSignal(TaskId taskId, string name, DurableTaskResponse value, string payloadFingerprint)
    {
        var recordId = GetSignalRecordId(taskId, name);
        if (_storage.TryGetTask(recordId, out var record))
        {
            return string.Equals(record.RequestFingerprint, payloadFingerprint, StringComparison.Ordinal)
                ? null
                : new InvalidOperationException(
                    $"Durable task '{taskId}' already received a different value for signal '{name}'.");
        }

        if (!_storage.TryGetTask(taskId, out var task) || !IsInvoked(task))
        {
            return new DurableTaskNotFoundException(taskId);
        }

        if (task.CompletedAt.HasValue)
        {
            return new InvalidOperationException(
                $"Durable task '{taskId}' has completed, so it no longer receives signal '{name}'.");
        }

        record = _storage.GetOrCreateTask(recordId, request: null);
        _storage.SetRequestFingerprint(recordId, record, payloadFingerprint);
        _storage.SetResponse(recordId, record, value);
        _pendingHandleResponses[recordId] = value;
        return null;
    }

    private static void ValidateSignal(TaskId taskId, string name, DurableTaskResponse value)
    {
        if (taskId.IsDefault)
        {
            throw new ArgumentException("A signal requires a task identifier.", nameof(taskId));
        }

        ArgumentException.ThrowIfNullOrEmpty(name);
        if (value is not { ResponseKind: DurableTaskResponseKind.CompletedSuccessfully })
        {
            throw new ArgumentException("A signal's value is a successful response.", nameof(value));
        }
    }

    /// <summary>Returns the invoked task whose execution tree contains <paramref name="executionId"/>.</summary>
    private TaskId GetSignalOwner(TaskId executionId)
    {
        for (var taskId = executionId; !taskId.IsDefault; taskId = taskId.Parent())
        {
            if (_storage.TryGetTask(taskId, out var state) && IsInvoked(state))
            {
                return taskId;
            }
        }

        throw new InvalidOperationException($"Durable execution '{executionId}' belongs to no invoked task, which receives signals.");
    }

    private static TaskId GetSignalRecordId(TaskId taskId, string name) => taskId.Child(SignalSegmentPrefix + name);

    private string GetSignalFingerprint(DurableTaskResponse value) =>
        SignalFingerprintPrefix + Convert.ToHexString(SHA256.HashData(_shared.Serializer.SerializeToArray(value)));

    private static string GetSignalSendFingerprint(GrainId target, TaskId taskId, string name, string payloadFingerprint)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var value in (ReadOnlySpan<string>)[target.ToString(), taskId.ToString(), name, payloadFingerprint])
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return SignalSendFingerprintPrefix + Convert.ToHexString(hash.GetHashAndReset());
    }
}
