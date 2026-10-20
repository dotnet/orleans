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
/// operation, a stable hash of the state's name, and the access's outcome, which is a read's value, a write's result, or
/// the exception which either threw. Outcomes are encoded with the Orleans serializer when the access runs, so later
/// in-place changes to a mutable value don't change its record. An exception whose encoding fails is normalized to a
/// description which both the recording attempt and replay throw. A write without a result records no value.
/// </para>
/// <para>
/// A write's record is staged with the write, in the same work item, so whichever journal write commits the change also
/// commits its record. A read's record is staged with the attempt's next write or recorded operation, which are the only
/// ways a decision based on the read becomes durable; reads which the body's tail makes, after its last durable operation,
/// are never staged. A replay consumes the records in order, checking each access's kind, operation, and state, and runs
/// live once it has consumed them all. An access which differs from its record, or whose recorded outcome no longer
/// decodes, diverges: the runtime stalls the attempt.
/// </para>
/// <para>
/// Accesses which run while the log runs an access or the runtime runs one of the attempt's operations belong to that
/// access or operation, so the provider supplies no context for them while <see cref="Depth"/> is above zero. The runtime
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
    TaskId taskId,
    IDurableTaskAccessLogStorage storage,
    Serializer serializer,
    TaskScheduler scheduler,
    Func<DurableTaskDivergenceException, Exception> diverge) : JournaledStateContext
{
    // Each record: [kind | flags][operation][state:4], then [length:4][outcome] when it has an outcome.
    private const byte KindMask = 0x03;
    private const byte HasValue = 0x04;
    private const byte IsException = 0x08;

    // Bits which no record of this version sets: a record which sets one has a representation which this reader can't
    // decode, so it diverges rather than being read as an outcome.
    private const byte ReservedFlags = unchecked((byte)~(KindMask | HasValue | IsException));
    private const int HeaderLength = 6;

    private readonly TaskId _taskId = taskId;
    private readonly IDurableTaskAccessLogStorage _storage = storage;
    private readonly Serializer _serializer = serializer;
    private readonly TaskScheduler _scheduler = scheduler;
    private readonly Func<DurableTaskDivergenceException, Exception> _diverge = diverge;

    // The records which earlier attempts staged, and the position of the next one which this attempt consumes.
    private byte[] _recorded = storage.GetAccessLog(taskId);
    private int _position;
    private int _consumed;

    // The records which this attempt made and hasn't staged yet: reads which no later write or operation depended on.
    private ArrayBufferWriter<byte>? _pending;

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

    /// <summary>
    /// Gets the number of accesses and runtime operations of the attempt which are running. Accesses made meanwhile belong
    /// to them.
    /// </summary>
    public int Depth { get; private set; }

    /// <summary>Gets whether the attempt hasn't yet consumed every access which earlier attempts recorded.</summary>
    public bool IsReplaying => _position < _recorded.Length;

    /// <summary>
    /// Marks the runtime's synchronous work on behalf of the attempt, such as starting a child, until the returned value is
    /// disposed: the accesses to journaled state which it makes belong to that operation.
    /// </summary>
    public OperationScope EnterOperation()
    {
        Depth++;
        return new(this);
    }

    /// <summary>Stages the records of the reads which the attempt made since its last write or recorded operation.</summary>
    /// <remarks>The attempt calls this as it reaches a recorded operation, which can depend on those reads.</remarks>
    public void Flush()
    {
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

    protected override TResult Access<TState, TResult>(
        in JournaledStateAccess access,
        TState state,
        Func<TState, TResult> operation,
        Action<TState, TResult>? apply)
    {
        Debug.Assert(TaskScheduler.Current == _scheduler, "A durable task body accesses journaled state on its activation's scheduler.");
        Debug.Assert(!access.Target.Name.StartsWith("$task", StringComparison.Ordinal), "The runtime's own state belongs to its operations, not to the body's accesses.");
        Depth++;
        try
        {
            if (_state is AccessLogState.Discarding)
            {
                return access.Kind is JournaledStateAccessKind.Write ? throw _discarded!() : operation(state);
            }

            if (IsReplaying)
            {
                return Replay<TResult>(access);
            }

            if (_state is AccessLogState.Closed)
            {
                return RunLive(state, operation, apply);
            }

            return Record(access, state, operation, apply);
        }
        finally
        {
            Depth--;
        }
    }

    private static TResult RunLive<TState, TResult>(TState state, Func<TState, TResult> operation, Action<TState, TResult>? apply)
    {
        var result = operation(state);
        apply?.Invoke(state, result);
        return result;
    }

    private TResult Record<TState, TResult>(
        in JournaledStateAccess access,
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
            RecordAndThrowException(access, exception);
            throw;
        }

        // The runtime can end the attempt while the operation runs, such as from a callback which it calls.
        if (IsOpen)
        {
            AddRecord(access, value is null ? (byte)0 : HasValue, value);
        }

        return result;
    }

    // The cancellation of the attempt ends it, so it's no outcome of the access which every later attempt observes.
    private static bool IsRecordable(Exception exception) => exception is not DurableTaskAttemptCanceledException;

    private TResult Replay<TResult>(in JournaledStateAccess access)
    {
        var index = ++_consumed;
        var header = _recorded.AsSpan(_position);
        TResult result = default!;
        Exception? exception = null;
        byte flags;
        byte operation;
        int target;
        try
        {
            flags = header[0];
            if ((flags & ReservedFlags) != 0)
            {
                throw new NotSupportedException($"The recorded access {index} has a representation, 0x{flags:x2}, which this version doesn't read.");
            }

            operation = header[1];
            target = BinaryPrimitives.ReadInt32LittleEndian(header[2..]);
            _position += HeaderLength;
            if ((flags & HasValue) != 0)
            {
                var length = BinaryPrimitives.ReadInt32LittleEndian(_recorded.AsSpan(_position));
                var value = new ReadOnlyMemory<byte>(_recorded, _position + 4, length);
                _position += 4 + length;
                if (Matches(access, flags, operation, target))
                {
                    if ((flags & IsException) != 0)
                    {
                        exception = _serializer.Deserialize<Exception>(value)!;
                    }
                    else if (typeof(TResult) == typeof(JournaledStateAccessNoResult))
                    {
                        throw new InvalidOperationException($"The recorded access {index} has a result, which access '{access.Operation}' doesn't return.");
                    }
                    else
                    {
                        result = _serializer.Deserialize<TResult>(value)!;
                    }
                }
            }
            else if (typeof(TResult) != typeof(JournaledStateAccessNoResult) && Matches(access, flags, operation, target))
            {
                throw new InvalidOperationException($"The recorded access {index} has no result, which access '{access.Operation}' returns.");
            }
        }
        catch (Exception decodeFailure)
        {
            throw _diverge(new DurableTaskDivergenceException(
                $"Durable task '{_taskId}' diverged from its recorded execution: the recorded outcome of its access {index} to journaled state '{access.Target.Name}' doesn't decode as the result of '{access.Operation}'. A replay accesses the same state with the same types; change a workflow's accesses only in new workflows, or after its executions complete.",
                decodeFailure));
        }

        if (!Matches(access, flags, operation, target))
        {
            throw _diverge(new DurableTaskDivergenceException(
                $"Durable task '{_taskId}' diverged from its recorded execution. Its access {index} to journaled state was recorded as "
                + $"{(JournaledStateAccessKind)(flags & KindMask)} '{(JournaledStateOperation)operation}' of state #{target:x8}, and this attempt performed "
                + $"{access.Kind} '{access.Operation}' of state '{access.Target.Name}' (#{access.Target.Id:x8}). A replay accesses the same state in the same "
                + "order; change a workflow's accesses only in new workflows, or after its executions complete."));
        }

        if (_position == _recorded.Length)
        {
            _recorded = [];
            _position = 0;
        }

        if (exception is not null)
        {
            throw exception;
        }

        return result!;
    }

    private static bool Matches(in JournaledStateAccess access, byte flags, byte operation, int target) =>
        (flags & KindMask) == (byte)access.Kind && operation == (byte)access.Operation && target == access.Target.Id;

    // A value which doesn't serialize throws, and the caller records that failure, so every attempt observes it.
    private byte[]? EncodeResult<TResult>(TResult result) =>
        typeof(TResult) == typeof(JournaledStateAccessNoResult) ? null : _serializer.SerializeToArray(result);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An exception which doesn't serialize is recorded as a description of it.")]
    [DoesNotReturn]
    private void RecordAndThrowException(in JournaledStateAccess access, Exception exception)
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

        AddRecord(access, HasValue | IsException, value);
        ExceptionDispatchInfo.Throw(exception);
    }

    private void AddRecord(in JournaledStateAccess access, byte flags, byte[]? value)
    {
        var pending = _pending ??= new();
        var span = pending.GetSpan(HeaderLength + 4 + (value?.Length ?? 0));
        span[0] = (byte)((byte)access.Kind | flags);
        span[1] = (byte)access.Operation;
        BinaryPrimitives.WriteInt32LittleEndian(span[2..], access.Target.Id);
        var length = HeaderLength;
        if (value is not null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[HeaderLength..], value.Length);
            value.CopyTo(span[(HeaderLength + 4)..]);
            length += 4 + value.Length;
        }

        pending.Advance(length);

        // A write's record commits with the write, together with the reads which it can depend on.
        if (access.Kind is JournaledStateAccessKind.Write)
        {
            Flush();
        }
    }

    /// <summary>Ends a runtime operation which <see cref="EnterOperation"/> marked.</summary>
    public readonly struct OperationScope(DurableTaskAccessLog? log) : IDisposable
    {
        /// <inheritdoc/>
        public void Dispose()
        {
            if (log is not null)
            {
                log.Depth--;
            }
        }
    }

    private enum AccessLogState : byte
    {
        Recording,
        Closed,
        Discarding,
    }
}
