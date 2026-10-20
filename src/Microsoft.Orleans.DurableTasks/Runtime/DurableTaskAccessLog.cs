using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Orleans.Journaling;
using Orleans.Serialization;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Records the accesses to journaled state which an attempt of a durable task's body makes, and returns the recorded
/// outcomes to later attempts, so a replay observes the values which the recording attempt observed and applies no change
/// twice.
/// </summary>
/// <remarks>
/// <para>
/// The log holds one record per access, in the order in which the body made them: whether it read or wrote, the
/// operation, a stable hash of the state's name, a stable hash of the access's arguments when it has any, such as a key,
/// an index or a written value (<see cref="DurableTaskAccessArgumentHash"/>), and the access's outcome, which is a read's
/// value, a write's result, or the exception which either threw. Outcomes are encoded with the Orleans serializer when the
/// access runs, so later in-place changes to a mutable value don't change its record. An exception whose encoding fails
/// is normalized to a description which both the recording attempt and replay throw. A write without a result records
/// no value.
/// </para>
/// <para>
/// A record is <c>[kind | flags:1][operation:1][state:4]</c>, then <c>[arguments:4]</c> when the flags have
/// <c>0x10</c>, then <c>[length:4][outcome]</c> when they have <c>0x04</c>; <c>0x08</c> marks an outcome which is an
/// exception, integers are little-endian, and a record which sets any other flag diverges.
/// </para>
/// <para>
/// A write's record is staged with the write, in the same work item, so whichever journal write commits the change also
/// commits its record. A read's record is staged with the attempt's next write, or by the storage as the next journal
/// write captures, which is the only way a decision based on the read becomes durable; reads which no write captures
/// before the body completes are dropped with its log. A replay consumes the records in order, checking each access's kind, operation, state, and
/// arguments, and runs live once it has consumed them all. An access which differs from its record, or whose recorded
/// outcome no longer decodes or names an exception type which this version can't construct, diverges: the runtime stalls
/// the attempt.
/// </para>
/// <para>
/// Each access runs in a covered region of the attempt (<see cref="DurableTaskContext.IsInCoveredRegion"/>), as effects
/// and the runtime's operations on behalf of the attempt do: the record covers the code which the access runs, such as a
/// comparer or an enumerated source, so the provider supplies no context for that code's accesses, and durable operations
/// within it throw. The runtime
/// closes the log when the attempt ends in a way which it records, so no later attempt replays the body: the body
/// completed, or the task was aborted or deleted. The attempt's later accesses then return the outcomes of the records
/// which it hasn't consumed yet, and run against the state directly, unrecorded, after that. Recorded task cancellation
/// doesn't close the log: the body's compensation is part of the execution's history, recorded and replayed as any of its
/// accesses. The runtime discards the attempt when it ends in a way which it doesn't record, such as the activation
/// stopping or a stall: the next attempt reruns the body from the staged records, so the discarded attempt's writes throw
/// its cancellation and its reads run against the state directly, unrecorded.
/// </para>
/// </remarks>
internal sealed class DurableTaskAccessLog(
    DurableTaskContext attempt,
    IDurableTaskAccessLogStorage storage,
    Serializer serializer,
    TaskScheduler scheduler,
    Func<DurableTaskDivergenceException, Exception> diverge) : JournaledStateContext, IDurableTaskPendingAccessRecords
{
    // Each record: [kind | flags][operation][state:4], then [hash:4] when it has an argument hash, then
    // [length:4][outcome] when it has an outcome.
    private const byte KindMask = 0x03;
    private const byte HasValue = 0x04;
    private const byte IsException = 0x08;
    private const byte HasArgumentHash = 0x10;

    // Bits which no record of this version sets: a record which sets one has a representation which this reader can't
    // decode, so it diverges rather than being read as an outcome.
    private const byte ReservedFlags = unchecked((byte)~(KindMask | HasValue | IsException | HasArgumentHash));
    private const int HeaderLength = 6;

    private readonly DurableTaskContext _attempt = attempt;
    private readonly TaskId _taskId = attempt.Id;
    private readonly IDurableTaskAccessLogStorage _storage = storage;
    private readonly Serializer _serializer = serializer;
    private readonly TaskScheduler _scheduler = scheduler;
    private readonly Func<DurableTaskDivergenceException, Exception> _diverge = diverge;

    // The records which earlier attempts staged, and the position of the next one which this attempt consumes.
    private byte[] _recorded = storage.GetAccessLog(attempt.Id);
    private int _position;
    private int _consumed;

    // The records which this attempt made and hasn't staged yet: reads which no later write depended on, which the
    // storage stages as its next write captures.
    private ArrayBufferWriter<byte>? _pending;
    private bool _addedPending;

    // Whether an access runs, which no journal write's capture interrupts.
    private bool _accessing;

    private AccessLogState _state;

    // Creates the exception which a discarded attempt's writes throw.
    private Func<Exception>? _discarded;

    /// <summary>Gets whether the runtime still records the attempt's accesses.</summary>
    public bool IsOpen => _state is AccessLogState.Recording;

    /// <summary>
    /// Gets whether the attempt's accesses run through this log: while it records them, while it returns the records which
    /// earlier attempts staged, and while it discards the attempt.
    /// </summary>
    public bool IsActive => _state is not AccessLogState.Closed || IsReplaying;

    /// <summary>Gets whether the attempt hasn't yet consumed every access which earlier attempts recorded.</summary>
    public bool IsReplaying => _position < _recorded.Length;

    /// <summary>Stages the records of the reads which the attempt made since its last write.</summary>
    /// <remarks>
    /// A write of the attempt stages them before its own record, and the storage stages them as a journal write captures,
    /// which is when any durable consequence of a read commits. The activation runs one work item at a time, so a write
    /// never captures while an access, or the code which it covers, runs.
    /// </remarks>
    public void Flush()
    {
        _addedPending = false;
        if (_pending is { WrittenCount: > 0 } pending)
        {
            _storage.AppendAccessLog(_taskId, pending.WrittenSpan);
            pending.ResetWrittenCount();
        }
    }

    /// <summary>
    /// Stops recording, because the attempt ended in a way which the runtime records: its body completed, or its task was
    /// aborted or deleted, so no later attempt replays it.
    /// </summary>
    /// <remarks>
    /// The attempt's later accesses, such as its cleanup's, first return the outcomes of the records which earlier attempts
    /// staged and which it hasn't consumed yet, because those changes already committed; after that, they run against the
    /// state directly and aren't recorded. The reads which the attempt made but didn't stage are dropped.
    /// </remarks>
    public void Close()
    {
        if (_state is AccessLogState.Recording)
        {
            _state = AccessLogState.Closed;
            _pending = null;
        }
    }

    /// <summary>
    /// Discards the attempt, which ended in a way which the runtime doesn't record, such as its activation stopping or a
    /// stall: a later attempt runs the body again from the records which this one staged.
    /// </summary>
    /// <param name="canceled">Creates the exception which the attempt's later writes throw: its cancellation.</param>
    /// <remarks>
    /// The attempt's later code, such as its cleanup, isn't part of the execution's history: recording it would diverge
    /// from the next attempt, and applying it would apply it a second time once the next attempt reaches the same point.
    /// Its writes therefore throw <paramref name="canceled"/>'s exception without changing the state, and its reads run
    /// against the state directly, unrecorded. The reads which the attempt made but didn't stage are dropped.
    /// </remarks>
    public void Discard(Func<Exception> canceled)
    {
        if (_state is AccessLogState.Recording)
        {
            _state = AccessLogState.Discarding;
            _discarded = canceled;
            _pending = null;
        }
    }

    protected override TResult Access<TArgument, TState, TResult>(
        in JournaledStateAccess access,
        TArgument argument,
        TState state,
        Func<TState, TResult> operation,
        Action<TState, TResult>? apply)
    {
        Debug.Assert(TaskScheduler.Current == _scheduler, "A durable task body accesses journaled state on its activation's scheduler.");
        using var region = _attempt.EnterCoveredRegion();
        _accessing = true;
        try
        {
            return RunAccess(access, argument, state, operation, apply);
        }
        finally
        {
            _accessing = false;
        }
    }

    void IDurableTaskPendingAccessRecords.Flush()
    {
        Debug.Assert(!_accessing, "A journal write captures between accesses: the activation runs one work item at a time.");
        Flush();
    }

    private TResult RunAccess<TArgument, TState, TResult>(
        in JournaledStateAccess access,
        TArgument argument,
        TState state,
        Func<TState, TResult> operation,
        Action<TState, TResult>? apply)
    {
        if (_state is AccessLogState.Discarding)
        {
            return access.Kind is JournaledStateAccessKind.Write ? throw _discarded!() : operation(state);
        }

        if (IsReplaying)
        {
            return Replay<TResult>(access, ArgumentHash(argument));
        }

        if (_state is AccessLogState.Closed)
        {
            return RunLive(state, operation, apply);
        }

        return Record(access, ArgumentHash(argument), state, operation, apply);
    }

    private static int? ArgumentHash<TArgument>(TArgument argument) =>
        DurableTaskAccessArgumentHash.TryCompute(argument, out var hash) ? hash : null;

    private static TResult RunLive<TState, TResult>(TState state, Func<TState, TResult> operation, Action<TState, TResult>? apply)
    {
        var result = operation(state);
        apply?.Invoke(state, result);
        return result;
    }

    private TResult Record<TState, TResult>(
        in JournaledStateAccess access,
        int? argumentHash,
        TState state,
        Func<TState, TResult> operation,
        Action<TState, TResult>? apply)
    {
        TResult result;
        byte[]? value;
        try
        {
            result = operation(state);

            // A change whose result is known first records it before applying, so a result which can't be recorded
            // leaves the state unchanged.
            value = EncodeResult(result);
            apply?.Invoke(state, result);
        }
        catch (Exception exception) when (IsOpen && IsRecordable(exception))
        {
            RecordAndThrowException(access, argumentHash, exception);
            throw;
        }

        // The runtime can end the attempt while the operation runs, such as from a callback which it calls.
        if (IsOpen)
        {
            AddRecord(access, argumentHash, value is null ? (byte)0 : HasValue, value);
        }

        return result;
    }

    // The cancellation of the attempt ends it, so it's no outcome of the access which every later attempt observes.
    private static bool IsRecordable(Exception exception) => exception is not DurableTaskAttemptCanceledException;

    private TResult Replay<TResult>(in JournaledStateAccess access, int? argumentHash)
    {
        var index = ++_consumed;
        var record = _recorded.AsSpan(_position);
        byte flags;
        byte operation;
        int target;
        int? recordedHash = null;
        ReadOnlyMemory<byte> value = default;
        var length = HeaderLength;
        try
        {
            flags = record[0];
            if ((flags & ReservedFlags) != 0)
            {
                throw Diverge(index, access, $"has a representation, 0x{flags:x2}, which this version doesn't read");
            }

            operation = record[1];
            target = BinaryPrimitives.ReadInt32LittleEndian(record[2..]);
            if ((flags & HasArgumentHash) != 0)
            {
                recordedHash = BinaryPrimitives.ReadInt32LittleEndian(record[length..]);
                length += 4;
            }

            if ((flags & HasValue) != 0)
            {
                var valueLength = BinaryPrimitives.ReadInt32LittleEndian(record[length..]);
                _ = record.Slice(length + 4, valueLength);
                value = new(_recorded, _position + length + 4, valueLength);
                length += 4 + valueLength;
            }
        }
        catch (Exception truncated) when (truncated is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw _diverge(new DurableTaskDivergenceException(
                $"Durable task '{_taskId}' diverged from its recorded execution: its recorded access {index} to journaled state is truncated.",
                truncated));
        }

        if ((flags & KindMask) != (byte)access.Kind || operation != (byte)access.Operation || target != access.Target.Id)
        {
            throw Diverge(
                index,
                access,
                $"was recorded as {(JournaledStateAccessKind)(flags & KindMask)} '{(JournaledStateOperation)operation}' of state #{target:x8}");
        }

        if (recordedHash != argumentHash)
        {
            throw Diverge(index, access, "was recorded with other arguments");
        }

        _position += length;
        if (_position == _recorded.Length)
        {
            _recorded = [];
            _position = 0;
        }

        var isException = (flags & IsException) != 0;
        var returnsResult = typeof(TResult) != typeof(JournaledStateAccessNoResult);
        if ((flags & HasValue) == 0 ? returnsResult || isException : !returnsResult && !isException)
        {
            throw Diverge(index, access, returnsResult ? "has no result, which the access returns" : "has a result, which the access doesn't return");
        }

        Exception? exception = null;
        TResult result = default!;
        try
        {
            if (isException)
            {
                exception = _serializer.Deserialize<Exception>(value)!;
            }
            else if (returnsResult)
            {
                result = _serializer.Deserialize<TResult>(value)!;
            }
        }
        catch (Exception decodeFailure)
        {
            throw _diverge(new DurableTaskDivergenceException(
                $"Durable task '{_taskId}' diverged from its recorded execution: the recorded outcome of its access {index} to journaled state '{access.Target.Name}' doesn't decode as the result of '{access.Operation}'. A replay accesses the same state with the same types; change a workflow's accesses only in new workflows, or after its executions complete.",
                decodeFailure));
        }

        // A recorded exception whose type this version can't resolve or construct would take other catch blocks than
        // the recording attempt's, so it diverges, as an outcome which doesn't decode does.
        if (exception is UnavailableExceptionFallbackException fallback)
        {
            throw _diverge(new DurableTaskDivergenceException(
                $"Durable task '{_taskId}' diverged from its recorded execution: the recorded exception of its access {index} to journaled state '{access.Target.Name}' has a type which this version can't construct.",
                fallback));
        }

        return exception is null ? result : throw exception;
    }

    private Exception Diverge(int index, in JournaledStateAccess access, string recorded) =>
        _diverge(new DurableTaskDivergenceException(
            $"Durable task '{_taskId}' diverged from its recorded execution. Its access {index} to journaled state {recorded}, and this attempt performed "
            + $"{access.Kind} '{access.Operation}' of state '{access.Target.Name}' (#{access.Target.Id:x8}). A replay accesses the same state with the same "
            + "arguments in the same order; change a workflow's accesses only in new workflows, or after its executions complete."));

    // A value which doesn't serialize throws, and the caller records that failure, so every attempt observes it.
    private byte[]? EncodeResult<TResult>(TResult result) =>
        typeof(TResult) == typeof(JournaledStateAccessNoResult) ? null : _serializer.SerializeToArray(result);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An exception which doesn't serialize is recorded as a description of it.")]
    [DoesNotReturn]
    private void RecordAndThrowException(in JournaledStateAccess access, int? argumentHash, Exception exception)
    {
        byte[] value;
        try
        {
            value = _serializer.SerializeToArray(exception);
        }
        catch (Exception serializationException)
        {
            exception = new InvalidOperationException(
                $"The access to journaled state '{access.Target.Name}' threw {exception.GetType()}: {exception.Message}, which couldn't be recorded: {serializationException.Message}");
            value = _serializer.SerializeToArray(exception);
        }

        AddRecord(access, argumentHash, HasValue | IsException, value);
        ExceptionDispatchInfo.Throw(exception);
    }

    private void AddRecord(in JournaledStateAccess access, int? argumentHash, byte flags, byte[]? value)
    {
        var pending = _pending ??= new();
        var span = pending.GetSpan(HeaderLength + 8 + (value?.Length ?? 0));
        if (argumentHash is not null)
        {
            flags |= HasArgumentHash;
        }

        span[0] = (byte)((byte)access.Kind | flags);
        span[1] = (byte)access.Operation;
        BinaryPrimitives.WriteInt32LittleEndian(span[2..], access.Target.Id);
        var length = HeaderLength;
        if (argumentHash is { } hash)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[length..], hash);
            length += 4;
        }

        if (value is not null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[length..], value.Length);
            value.CopyTo(span[(length + 4)..]);
            length += 4 + value.Length;
        }

        pending.Advance(length);

        // A write's record commits with the write, together with the reads which it can depend on. A read's record commits
        // with the next write which captures, unless the attempt's own write stages it first.
        if (access.Kind is JournaledStateAccessKind.Write)
        {
            Flush();
        }
        else if (!_addedPending)
        {
            _addedPending = true;
            _storage.AddPendingRecords(this);
        }
    }

    private enum AccessLogState : byte
    {
        Recording,
        Closed,
        Discarding,
    }
}
