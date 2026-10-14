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
/// record, is rejected. A signal which the inbox delivers ahead of its invocation is recorded for that invocation.
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
    /// <param name="throwOnRejection">
    /// <see langword="true"/> to throw when this grain's task rejects the signal, without recording the send; otherwise the
    /// marker records the rejection, which the operation returns, so a replay returns the same result.
    /// </param>
    /// <remarks>
    /// A signal for this grain is staged directly in task state; one for another grain is staged in the outbox, whose
    /// target's inbox records the value or dead-letters a rejected signal, so the send reports
    /// <see cref="DurableSignalResult.Accepted"/>. A replay which reaches the marker sends nothing again, and the marker is
    /// deleted with the execution's tree.
    /// </remarks>
    public async ValueTask<DurableSignalResult> SendSignalAsync(
        TaskId markerId,
        GrainId target,
        TaskId taskId,
        string name,
        DurableTaskResponse value,
        bool throwOnRejection,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSignal(taskId, name, value);
        var payloadFingerprint = GetSignalFingerprint(value);
        var markerFingerprint = GetSignalSendFingerprint(target, taskId, name, payloadFingerprint);
        DurableSignalResult result;
        if (_storage.TryGetTask(markerId, out var recorded))
        {
            if (!string.Equals(recorded.RequestFingerprint, markerFingerprint, StringComparison.Ordinal))
            {
                throw CreateDivergenceException(
                    $"Durable task '{markerId}' is already associated with another operation.",
                    recorded,
                    $"sending signal '{name}' to task '{taskId}' on grain '{target}'");
            }

            // The marker commits with the send, possibly in a write which an earlier attempt is still awaiting.
            await WriteStateAsync(cancellationToken);
            result = recorded.Result!.Result is DurableSignalResult recordedResult ? recordedResult : DurableSignalResult.Accepted;
            if (throwOnRejection && CreateSignalRejection(taskId, name, result) is { } recordedRejection)
            {
                throw recordedRejection;
            }

            return result;
        }

        result = DurableSignalResult.Accepted;
        if (target == GrainId)
        {
            result = StageSignal(taskId, name, value, payloadFingerprint);
            if (throwOnRejection && CreateSignalRejection(taskId, name, result) is { } rejection)
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
        _storage.SetResponse(
            markerId,
            marker,
            result == DurableSignalResult.Accepted ? DurableTaskResponse.Completed : DurableTaskResponse.FromResult(result));
        await WriteStateAsync(cancellationToken);
        return result;
    }

    /// <summary>Stages a signal received through the inbox, completing its waits after the inbox batch commits.</summary>
    /// <returns><see langword="null"/> when the signal is accepted, or the reason it is rejected.</returns>
    internal Exception? StageSignalFromInbox(TaskId taskId, string name, DurableTaskResponse value)
    {
        ThrowIfStopping();
        ValidateSignal(taskId, name, value);
        return CreateSignalRejection(taskId, name, StageSignal(taskId, name, value, GetSignalFingerprint(value), fromInbox: true));
    }

    async ValueTask IDurableTaskGrainExtension.SignalAsync(
        TaskId taskId,
        string name,
        DurableTaskResponse value,
        CancellationToken cancellationToken) =>
        await SignalOwnTaskAsync(taskId, name, value, throwOnRejection: true, cancellationToken);

    ValueTask<DurableSignalResult> IDurableTaskGrainExtension.TrySignalAsync(
        TaskId taskId,
        string name,
        DurableTaskResponse value,
        CancellationToken cancellationToken) =>
        SignalOwnTaskAsync(taskId, name, value, throwOnRejection: false, cancellationToken);

    async ValueTask IDurableTaskManager.SignalAsync<T>(
        TaskId taskId,
        DurableSignal<T> signal,
        T value,
        CancellationToken cancellationToken) =>
        await SignalFromManagerAsync(taskId, signal, value, throwOnRejection: true, cancellationToken);

    ValueTask<DurableSignalResult> IDurableTaskManager.TrySignalAsync<T>(
        TaskId taskId,
        DurableSignal<T> signal,
        T value,
        CancellationToken cancellationToken) =>
        SignalFromManagerAsync(taskId, signal, value, throwOnRejection: false, cancellationToken);

    private ValueTask<DurableSignalResult> SignalFromManagerAsync<T>(
        TaskId taskId,
        DurableSignal<T> signal,
        T value,
        bool throwOnRejection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var response = DurableTaskResponse.FromResult(value);

        // A durable execution records the send, so its replay doesn't depend on the target's current state.
        return DurableTaskContext.TryGetCurrent(out var current) && current is GrainDurableTaskContext context
            ? context.SendSignalAsync(GrainId, taskId, signal.Name, response, throwOnRejection, cancellationToken)
            : SignalOwnTaskAsync(taskId, signal.Name, response, throwOnRejection, cancellationToken);
    }

    /// <summary>Stages a signal for a task of this grain and waits for the journal write which commits it.</summary>
    /// <remarks>
    /// A duplicate also waits for that write, so the value is durable when the operation completes. A rejection stages
    /// nothing, and either throws or is returned.
    /// </remarks>
    private async ValueTask<DurableSignalResult> SignalOwnTaskAsync(
        TaskId taskId,
        string name,
        DurableTaskResponse value,
        bool throwOnRejection,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        ValidateSignal(taskId, name, value);
        var result = StageSignal(taskId, name, value, GetSignalFingerprint(value));
        if (result is DurableSignalResult.Accepted or DurableSignalResult.Duplicate)
        {
            await WriteStateAsync(cancellationToken);
        }
        else if (throwOnRejection)
        {
            throw CreateSignalRejection(taskId, name, result)!;
        }

        return result;
    }

    /// <summary>Records the first value of a signal for an invoked task, without writing.</summary>
    /// <param name="fromInbox">
    /// <see langword="true"/> when the signal was delivered through the inbox, so it can precede its invocation.
    /// </param>
    /// <returns>Whether the signal is recorded, is a duplicate, or why it is rejected; a rejection stages nothing.</returns>
    /// <remarks>
    /// A sender can commit an invocation and a signal for it in one write, and the outbox delivers them independently.
    /// A delivered signal for a task which isn't stored, or which records only a cancellation that preceded its
    /// invocation, is therefore recorded ahead of the invocation, which finds the record in its tree. The record is
    /// deleted once Messaging's <c>OutboxRetryTimeout</c> has elapsed without the invocation, after which the invocation
    /// can no longer arrive.
    /// </remarks>
    private DurableSignalResult StageSignal(
        TaskId taskId,
        string name,
        DurableTaskResponse value,
        string payloadFingerprint,
        bool fromInbox = false)
    {
        var recordId = GetSignalRecordId(taskId, name);
        if (_storage.TryGetTask(recordId, out var record))
        {
            return string.Equals(record.RequestFingerprint, payloadFingerprint, StringComparison.Ordinal)
                ? DurableSignalResult.Duplicate
                : DurableSignalResult.Conflict;
        }

        var found = _storage.TryGetTask(taskId, out var task);
        var precedesInvocation = fromInbox && (!found || IsCancellationRecord(task!));
        if (!precedesInvocation)
        {
            if (!found || !IsInvoked(task!))
            {
                return DurableSignalResult.TaskNotFound;
            }

            if (task!.CompletedAt.HasValue)
            {
                return DurableSignalResult.TaskCompleted;
            }
        }

        record = _storage.GetOrCreateTask(recordId, request: null);
        _storage.SetRequestFingerprint(recordId, record, payloadFingerprint);
        _storage.SetResponse(recordId, record, value);
        _pendingHandleResponses[recordId] = value;
        if (precedesInvocation)
        {
            TrackExpiry(GetEarlySignalExpiry(record));
        }

        return DurableSignalResult.Accepted;
    }

    /// <summary>Returns whether a task records a signal's value.</summary>
    private static bool IsSignalRecord(IDurableTaskState state) =>
        state.RequestFingerprint is { } fingerprint
        && fingerprint.StartsWith(SignalFingerprintPrefix, StringComparison.Ordinal);

    /// <summary>Returns when a signal which arrived before its invocation is deleted unless the invocation arrives.</summary>
    private DateTimeOffset GetEarlySignalExpiry(IDurableTaskState record) =>
        AddSaturating(record.CreatedAt, _shared.OutboxRetryTimeout);

    /// <summary>Creates the exception which reports a rejected signal, or returns <see langword="null"/> for a recorded one.</summary>
    private static Exception? CreateSignalRejection(TaskId taskId, string name, DurableSignalResult result) => result switch
    {
        DurableSignalResult.Conflict => new InvalidOperationException(
            $"Durable task '{taskId}' already received a different value for signal '{name}'."),
        DurableSignalResult.TaskNotFound => new DurableTaskNotFoundException(taskId),
        DurableSignalResult.TaskCompleted => new InvalidOperationException(
            $"Durable task '{taskId}' has completed, so it no longer receives signal '{name}'."),
        _ => null,
    };

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
