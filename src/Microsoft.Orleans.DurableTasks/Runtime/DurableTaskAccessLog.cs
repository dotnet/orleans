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
/// operation, the state, a stable hash of the access's arguments when it has any, such as a key, an index or a written
/// value (<see cref="DurableTaskAccessArgumentHash"/>), and the access's outcome, which is a read's value, a write's
/// result, or the exception which either threw. Outcomes are encoded with the Orleans serializer when the access runs, so
/// later in-place changes to a mutable value don't change its record. An exception whose encoding fails is normalized to a
/// description which both the recording attempt and replay throw. A write without a result records no value. A read
/// whose outcome repeats the latest read of the same state and operation which this attempt recorded with a value records
/// a reference to it instead: when its result is equal (for results which compare exactly, such as strings and
/// primitives), or when the state hasn't changed since and the read's argument is absent or equal. Consecutive references
/// to one read with the same arguments share one record.
/// </para>
/// <para>
/// A write's record is staged with the write, in the same work item, so whichever journal write commits the change also
/// commits its record. A read's record is staged with the attempt's next write, or by the storage as the next journal
/// write captures, which is the only way a decision based on the read becomes durable; reads which no write captures
/// before the body completes are dropped with its log. A replay consumes the records in order, checking each access's
/// kind, operation, state, and arguments, and runs live once it has consumed them all. An access which differs from its
/// record, or whose recorded outcome no longer decodes or names an exception type which this version can't construct,
/// diverges: the runtime stalls the attempt. A replayed reference returns the outcome of the read it refers to, as a live
/// repeat of an unchanged state does: the same result, or, for operations which return copies, a new copy which holds the
/// same elements.
/// </para>
/// <para>
/// Each access runs in a covered region of the attempt (<see cref="DurableTaskContext.IsInCoveredRegion"/>), as effects
/// and the runtime's operations on behalf of the attempt do: the record covers the code which the access runs, such as a
/// comparer or an enumerated source, so the provider supplies no context for that code's accesses, and durable operations
/// within it throw. The runtime closes the log when the attempt ends in a way which it records, so no later attempt
/// replays the body: the body completed, or the task was aborted or deleted. The attempt's later accesses then return the
/// outcomes of the records which it hasn't consumed yet, and run against the state directly, unrecorded, after that.
/// Recorded task cancellation doesn't close the log: the body's compensation is part of the execution's history, recorded
/// and replayed as any of its accesses. The runtime discards the attempt when it ends in a way which it doesn't record,
/// such as the activation stopping or a stall: the next attempt reruns the body from the staged records, so the discarded
/// attempt's writes throw its cancellation and its reads run against the state directly, unrecorded.
/// </para>
/// <para>
/// Each record has this format:
/// </para>
/// <code>
/// record    := header operation state [arguments] outcome
/// header    := one byte: bits 0-1 the access's kind (1 read, 2 write); 0x04 the record holds a value; 0x08 the value is
///              an exception (only with 0x04); 0x10 an argument hash follows; 0x20 the record refers to an earlier read;
///              0x40 is reserved and zero; 0x80 is set in every record of this format
/// operation := one byte, the JournaledStateOperation
/// state     := varint index into the log's table of states; the index which equals the table's size adds a state, whose
///              stable id, a little-endian int32, follows
/// arguments := the little-endian int32 hash of the access's arguments. A reference without one passes the arguments of
///              the read which it refers to.
/// outcome   := nothing (no value)
///            | varint length, then the Orleans-serialized value or exception (0x04)
///            | varint count of reads which repeat the latest read of the same state and operation recorded with a
///              value (0x20)
/// </code>
/// <para>
/// A replay stalls as a divergence on a record with another combination of bits, so a later version can add forms which an
/// earlier version never misreads.
/// </para>
/// </remarks>
internal sealed class DurableTaskAccessLog(
    DurableTaskContext attempt,
    IDurableTaskAccessLogStorage storage,
    Serializer serializer,
    TaskScheduler scheduler,
    Func<DurableTaskDivergenceException, Exception> diverge) : JournaledStateContext, IDurableTaskPendingAccessRecords
{
    private const byte KindMask = 0x03;
    private const byte HasValue = 0x04;
    private const byte IsException = 0x08;
    private const byte HasArgumentHash = 0x10;
    private const byte IsReference = 0x20;
    private const byte Format = 0x80;
    private const byte FormMask = HasValue | IsException | IsReference;
    private const byte ReservedBits = 0x40;

    // The longest header: kind and flags, operation, a five-byte varint index, a state's id, and an argument hash.
    private const int MaxHeaderLength = 2 + 5 + 4 + 4;

    private readonly DurableTaskContext _attempt = attempt;
    private readonly TaskId _taskId = attempt.Id;
    private readonly IDurableTaskAccessLogStorage _storage = storage;
    private readonly Serializer _serializer = serializer;
    private readonly TaskScheduler _scheduler = scheduler;
    private readonly Func<DurableTaskDivergenceException, Exception> _diverge = diverge;

    // The records which earlier attempts staged, the position of the next one which this attempt consumes, and the
    // number of accesses which it consumed.
    private ReadOnlyMemory<byte> _recorded = storage.GetAccessLog(attempt.Id);
    private int _position;
    private int _consumed;

    // The ids of the states which the log's records name, in the order in which they first appear; and the index of the
    // state which this attempt recorded last.
    private int[]? _states;
    private int _stateCount;
    private JournaledStateAccessTarget? _lastState;
    private int _lastStateIndex;

    // While replaying: the latest read of each state and operation which was recorded with its value, which references
    // refer to; and the run of references which the replay is consuming, with the argument hash of its accesses.
    private RecordedRead[]? _recordedReads;
    private int _recordedReadCount;
    private int _runRead;
    private int _runRemaining;
    private byte _runOperation;
    private int _runState;
    private int? _runHash;

    // The records which this attempt made and hasn't staged yet: reads which no later write depended on, which the
    // storage stages as its next write captures.
    private ArrayBufferWriter<byte>? _pending;
    private bool _addedPending;

    // Holds an outcome while it's encoded, before it's copied into its record.
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? t_encoded;

    // While recording: the latest read of each state and operation which this attempt recorded with its value, and the
    // run of references to one of them which the pending records end with, which isn't encoded yet. Replayed reads never
    // enter it: a reference refers only to a read of this attempt. A body reads few states, so the reads are searched in
    // turn.
    private LiveRead[]? _liveReads;
    private int _liveReadCount;
    private LiveRead? _lastLiveRead;
    private LiveRead? _openRun;
    private int? _openRunHash;
    private int _openRunCount;

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
    public bool IsReplaying => _runRemaining > 0 || _position < _recorded.Length;

    /// <summary>Stages the records of the reads which the attempt made since its last write.</summary>
    /// <remarks>
    /// A write of the attempt stages them before its own record, and the storage stages them as a journal write captures,
    /// which is when any durable consequence of a read commits. The activation runs one work item at a time, so a write
    /// never captures while an access, or the code which it covers, runs.
    /// </remarks>
    public void Flush()
    {
        EndRun();
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
            StopRecording();
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
            StopRecording();
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

    private void StopRecording()
    {
        _pending = null;
        _liveReads = null;
        _liveReadCount = 0;
        _lastLiveRead = null;
        _openRun = null;
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

        if (access.Kind is JournaledStateAccessKind.Read && LiveRead<TArgument, TResult>.CanRepeat)
        {
            Debug.Assert(apply is null, "A read changes no state.");
            return RecordRead(access, argument, state, operation);
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
        ArrayBufferWriter<byte>? encoded;
        byte[]? copied = null;
        try
        {
            result = operation(state);

            // A change whose result is known first records it before applying, so a result which can't be recorded
            // leaves the state unchanged.
            encoded = Encode(result);
            if (apply is not null)
            {
                // Applying runs other code, which could encode another outcome on this thread.
                copied = encoded?.WrittenSpan.ToArray();
                apply(state, result);
            }
        }
        catch (Exception exception) when (IsOpen && IsRecordable(exception))
        {
            RecordAndThrowException(access, argumentHash, exception);
            throw;
        }

        // The runtime can end the attempt while the operation runs, such as from a callback which it calls.
        if (IsOpen)
        {
            if (encoded is null)
            {
                AddRecord(access, argumentHash, 0);
            }
            else
            {
                AddRecord(access, argumentHash, HasValue, copied ?? encoded.WrittenSpan);
            }
        }

        return result;
    }

    // Records a read which can repeat an earlier read: as a reference when it does, otherwise with its value, as the latest
    // read of its state and operation.
    private TResult RecordRead<TArgument, TState, TResult>(
        in JournaledStateAccess access,
        TArgument argument,
        TState state,
        Func<TState, TResult> operation)
    {
        var argumentHash = ArgumentHash(argument);
        TResult result;
        ArrayBufferWriter<byte>? encoded = null;
        var target = access.Target;
        var earlier = _lastLiveRead is { } last && ReferenceEquals(last.Target, target) && last.Operation == access.Operation
            ? last
            : FindLiveRead(target, access.Operation);
        var typed = earlier as LiveRead<TArgument, TResult>;
        bool repeats;
        try
        {
            result = operation(state);
            repeats = typed?.Repeats(argument, result) is true;
            if (!repeats)
            {
                encoded = Encode(result);
            }
        }
        catch (Exception exception) when (IsOpen && IsRecordable(exception))
        {
            RecordAndThrowException(access, argumentHash, exception);
            throw;
        }

        if (!IsOpen)
        {
            return result;
        }

        if (repeats)
        {
            _lastLiveRead = typed;
            AddReference(typed!, argumentHash);
            return result;
        }

        AddRecord(access, argumentHash, HasValue, encoded!.WrittenSpan);
        if (typed is null)
        {
            typed = new(target, access.Operation);
            var reads = _liveReads ??= new LiveRead[4];
            if (_liveReadCount == reads.Length)
            {
                Array.Resize(ref _liveReads, reads.Length * 2);
            }

            _liveReads[_liveReadCount++] = typed;
        }

        typed.Set(argument, result, argumentHash);
        _lastLiveRead = typed;
        return result;
    }

    // The cancellation of the attempt ends it, so it's no outcome of the access which every later attempt observes.
    private static bool IsRecordable(Exception exception) => exception is not DurableTaskAttemptCanceledException;

    private LiveRead? FindLiveRead(JournaledStateAccessTarget target, JournaledStateOperation operation)
    {
        for (var i = 0; i < _liveReadCount; i++)
        {
            var read = _liveReads![i];
            if (ReferenceEquals(read.Target, target) && read.Operation == operation)
            {
                return read;
            }
        }

        return null;
    }

    private TResult Replay<TResult>(in JournaledStateAccess access, int? argumentHash)
    {
        var index = ++_consumed;
        if (_runRemaining > 0)
        {
            if ((byte)access.Kind != (byte)JournaledStateAccessKind.Read || (byte)access.Operation != _runOperation || access.Target.Id != _runState)
            {
                throw Diverge(index, access, $"was recorded as Read '{(JournaledStateOperation)_runOperation}' of state #{_runState:x8}");
            }

            if (argumentHash != _runHash)
            {
                throw Diverge(index, access, "was recorded with other arguments");
            }

            _runRemaining--;
            var repeated = ReadRepeated<TResult>(access, index, ref _recordedReads![_runRead]);
            ReleaseConsumedRecords();
            return repeated;
        }

        var position = _position;
        if (ReadHeader(ref position, out var flags, out var operation, out var stateIndex, out var recordedHash, out var count, out var value) is { } malformed)
        {
            throw Diverge(index, access, malformed);
        }

        var target = _states![stateIndex];
        if ((flags & KindMask) != (byte)access.Kind || operation != (byte)access.Operation || target != access.Target.Id)
        {
            throw Diverge(
                index,
                access,
                $"was recorded as {(JournaledStateAccessKind)(flags & KindMask)} '{(JournaledStateOperation)operation}' of state #{target:x8}");
        }

        if ((flags & IsReference) != 0)
        {
            var slot = FindRecordedRead(GetReadKey(stateIndex, operation));
            if (count is 0 or > int.MaxValue || access.Kind is not JournaledStateAccessKind.Read || slot < 0)
            {
                throw Diverge(index, access, $"repeats {count} times a read which the log doesn't hold");
            }

            // A reference without its own argument hash passes the arguments of the read which it refers to.
            var expectedHash = (flags & HasArgumentHash) != 0 ? recordedHash : _recordedReads![slot].Hash;
            if (expectedHash != argumentHash)
            {
                throw Diverge(index, access, "was recorded with other arguments");
            }

            _position = position;
            _runRead = slot;
            _runRemaining = (int)count - 1;
            _runOperation = operation;
            _runState = target;
            _runHash = expectedHash;
            var repeated = ReadRepeated<TResult>(access, index, ref _recordedReads![slot]);
            ReleaseConsumedRecords();
            return repeated;
        }

        if (recordedHash != argumentHash)
        {
            throw Diverge(index, access, "was recorded with other arguments");
        }

        _position = position;
        var isException = (flags & IsException) != 0;
        var returnsResult = typeof(TResult) != typeof(JournaledStateAccessNoResult);
        if ((flags & HasValue) == 0 ? returnsResult : !returnsResult && !isException)
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
            throw DecodeFailure(access, index, decodeFailure);
        }

        // A recorded exception whose type this version can't resolve or construct would take other catch blocks than
        // the recording attempt's, so it diverges, as an outcome which doesn't decode does.
        if (exception is UnavailableExceptionFallbackException fallback)
        {
            throw _diverge(new DurableTaskDivergenceException(
                $"Durable task '{_taskId}' diverged from its recorded execution: the recorded exception of its access {index} to journaled state '{access.Target.Name}' has a type which this version can't construct.",
                fallback));
        }

        if (exception is null && access.Kind is JournaledStateAccessKind.Read && returnsResult && _position < _recorded.Length)
        {
            RememberRead(stateIndex, access.Operation, value, recordedHash, result);
        }

        ReleaseConsumedRecords();
        return exception is null ? result : throw exception;
    }

    // Reads the header of the record at position and advances past it: its flags, operation, state, argument hash, and
    // the value or the count of references which follows. Returns why the record can't be read, or null.
    private string? ReadHeader(
        ref int position,
        out byte flags,
        out byte operation,
        out int stateIndex,
        out int? argumentHash,
        out uint count,
        out ReadOnlyMemory<byte> value)
    {
        var input = _recorded.Span;
        flags = 0;
        operation = 0;
        stateIndex = 0;
        argumentHash = null;
        count = 0;
        value = default;
        try
        {
            flags = input[position];
            var form = flags & FormMask;
            if ((flags & (ReservedBits | Format)) != Format || form is not (0 or HasValue or (HasValue | IsException) or IsReference))
            {
                return $"has a representation, 0x{flags:x2}, which this version doesn't read";
            }

            operation = input[position + 1];
            position += 2;
            stateIndex = checked((int)ReadVarUInt32(input, ref position));
            if (stateIndex == _stateCount)
            {
                AddState(BinaryPrimitives.ReadInt32LittleEndian(input[position..]));
                position += 4;
            }
            else if (stateIndex > _stateCount)
            {
                return $"names state {stateIndex}, which no earlier record named";
            }

            if ((flags & HasArgumentHash) != 0)
            {
                argumentHash = BinaryPrimitives.ReadInt32LittleEndian(input[position..]);
                position += 4;
            }

            if ((flags & HasValue) != 0)
            {
                var length = checked((int)ReadVarUInt32(input, ref position));
                value = _recorded.Slice(position, length);
                position += length;
            }
            else if ((flags & IsReference) != 0)
            {
                count = ReadVarUInt32(input, ref position);
            }

            return null;
        }
        catch (Exception malformed) when (malformed is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException or FormatException)
        {
            return $"is truncated or malformed ({malformed.Message})";
        }
    }

    // Returns the outcome of the recorded read which a reference refers to: the read's own result, which a live repeat of
    // an unchanged state returns too, or, for an operation which returns copies, a new copy which holds the same elements.
    private TResult ReadRepeated<TResult>(in JournaledStateAccess access, int index, ref RecordedRead read)
    {
        if (read.HasInstance)
        {
            return JournaledStateOperations.ReturnsCopy(access.Operation) ? (TResult)((Array)read.Instance!).Clone() : (TResult)read.Instance!;
        }

        TResult value;
        try
        {
            value = _serializer.Deserialize<TResult>(read.Value)!;
        }
        catch (Exception decodeFailure)
        {
            throw DecodeFailure(access, index, decodeFailure);
        }

        read.Instance = value;
        read.HasInstance = true;
        return value;
    }

    private void RememberRead<TResult>(int stateIndex, JournaledStateOperation operation, ReadOnlyMemory<byte> value, int? hash, TResult result)
    {
        var key = GetReadKey(stateIndex, (byte)operation);
        var slot = FindRecordedRead(key);
        if (slot < 0)
        {
            var reads = _recordedReads ??= new RecordedRead[4];
            if (_recordedReadCount == reads.Length)
            {
                Array.Resize(ref _recordedReads, reads.Length * 2);
            }

            slot = _recordedReadCount++;
            _recordedReads[slot].Key = key;
        }

        ref var read = ref _recordedReads![slot];
        read.Value = value;
        read.Hash = hash;
        if (JournaledStateOperations.ReturnsCopy(operation))
        {
            // The body can change the copy which it received, so references copy the elements as the read returned them.
            Debug.Assert(result is Array, "Operations which return copies return arrays.");
            read.Instance = ((Array)(object)result!).Clone();
            read.HasInstance = true;
        }
        else if (typeof(TResult).IsValueType && ExactEquality<TResult>.IsSupported)
        {
            // Nothing in the result has an identity, so a reference decodes it once, without boxing every replayed read.
            read.Instance = null;
            read.HasInstance = false;
        }
        else
        {
            read.Instance = result;
            read.HasInstance = true;
        }
    }

    // Once the attempt consumed every record, it no longer holds them.
    private void ReleaseConsumedRecords()
    {
        if (_runRemaining == 0 && _position == _recorded.Length)
        {
            _recorded = default;
            _position = 0;
            _recordedReads = null;
            _recordedReadCount = 0;
        }
    }

    // Returns the index of the recorded read of the state and operation which key names, or -1.
    private int FindRecordedRead(int key)
    {
        for (var i = 0; i < _recordedReadCount; i++)
        {
            if (_recordedReads![i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    private void AddState(int id)
    {
        var states = _states ??= new int[4];
        if (_stateCount == states.Length)
        {
            Array.Resize(ref _states, states.Length * 2);
        }

        _states[_stateCount++] = id;
    }

    private static int GetReadKey(int stateIndex, byte operation) => (stateIndex << 8) | operation;

    private Exception Diverge(int index, in JournaledStateAccess access, string recorded) =>
        _diverge(new DurableTaskDivergenceException(
            $"Durable task '{_taskId}' diverged from its recorded execution. Its access {index} to journaled state {recorded}, and this attempt performed "
            + $"{access.Kind} '{access.Operation}' of state '{access.Target.Name}' (#{access.Target.Id:x8}). A replay accesses the same state with the same "
            + "arguments in the same order; change a workflow's accesses only in new workflows, or after its executions complete."));

    private Exception DecodeFailure(in JournaledStateAccess access, int index, Exception decodeFailure) =>
        _diverge(new DurableTaskDivergenceException(
            $"Durable task '{_taskId}' diverged from its recorded execution: the recorded outcome of its access {index} to journaled state '{access.Target.Name}' doesn't decode as the result of '{access.Operation}'. A replay accesses the same state with the same types; change a workflow's accesses only in new workflows, or after its executions complete.",
            decodeFailure));

    // Encodes a result, or returns null for a write without one. A value which doesn't serialize throws, and the caller
    // records that failure, so every attempt observes it.
    private ArrayBufferWriter<byte>? Encode<TResult>(TResult result)
    {
        if (typeof(TResult) == typeof(JournaledStateAccessNoResult))
        {
            return null;
        }

        var encoded = t_encoded ??= new();
        encoded.ResetWrittenCount();
        _serializer.Serialize(result, encoded);
        return encoded;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An exception which doesn't serialize is recorded as a description of it.")]
    [DoesNotReturn]
    private void RecordAndThrowException(in JournaledStateAccess access, int? argumentHash, Exception exception)
    {
        var encoded = t_encoded ??= new();
        encoded.ResetWrittenCount();
        try
        {
            _serializer.Serialize(exception, encoded);
        }
        catch (Exception serializationException)
        {
            exception = new InvalidOperationException(
                $"The access to journaled state '{access.Target.Name}' threw {exception.GetType()}: {exception.Message}, which couldn't be recorded: {serializationException.Message}");
            encoded.ResetWrittenCount();
            _serializer.Serialize(exception, encoded);
        }

        AddRecord(access, argumentHash, HasValue | IsException, encoded.WrittenSpan);
        ExceptionDispatchInfo.Throw(exception);
    }

    private void AddRecord(in JournaledStateAccess access, int? argumentHash, byte flags, ReadOnlySpan<byte> value = default)
    {
        var pending = BeginRecord((byte)((byte)access.Kind | flags), (byte)access.Operation, access.Target, argumentHash);
        if ((flags & HasValue) != 0)
        {
            var span = pending.GetSpan(5 + value.Length);
            var length = WriteVarUInt32(span, (uint)value.Length);
            value.CopyTo(span[length..]);
            pending.Advance(length + value.Length);
        }

        // A write's record commits with the write, together with the reads which it can depend on. A read's record commits
        // with the next write which captures, unless the attempt's own write stages it first.
        if (access.Kind is JournaledStateAccessKind.Write)
        {
            Flush();
        }
        else
        {
            AddPending();
        }
    }

    private void AddPending()
    {
        if (!_addedPending)
        {
            _addedPending = true;
            _storage.AddPendingRecords(this);
        }
    }

    private void AddReference(LiveRead read, int? argumentHash)
    {
        if (ReferenceEquals(_openRun, read) && _openRunHash == argumentHash)
        {
            _openRunCount++;
            return;
        }

        EndRun();
        _openRun = read;
        _openRunHash = argumentHash;
        _openRunCount = 1;
        AddPending();
    }

    // Encodes the run of references which the pending records end with.
    private void EndRun()
    {
        if (_openRun is { } run)
        {
            _openRun = null;

            // References which pass the arguments of the read which they refer to don't repeat its hash.
            var ownHash = _openRunHash == run.Hash ? null : _openRunHash;
            var flags = (byte)((byte)JournaledStateAccessKind.Read | IsReference);
            var pending = BeginRecord(flags, (byte)run.Operation, run.Target, ownHash);
            pending.Advance(WriteVarUInt32(pending.GetSpan(5), (uint)_openRunCount));
        }
    }

    // Writes a record's header, state and argument hash, ending the run of references which precedes it.
    private ArrayBufferWriter<byte> BeginRecord(byte header, byte operation, JournaledStateAccessTarget target, int? argumentHash)
    {
        EndRun();
        var pending = _pending ??= new();
        var span = pending.GetSpan(MaxHeaderLength);
        span[0] = (byte)(header | Format | (argumentHash is null ? 0 : HasArgumentHash));
        span[1] = operation;
        var length = 2;
        if (ReferenceEquals(_lastState, target))
        {
            length += WriteVarUInt32(span[length..], (uint)_lastStateIndex);
        }
        else
        {
            var index = _states is null ? -1 : Array.IndexOf(_states, target.Id, 0, _stateCount);
            if (index < 0)
            {
                index = _stateCount;
                AddState(target.Id);
                length += WriteVarUInt32(span[length..], (uint)index);
                BinaryPrimitives.WriteInt32LittleEndian(span[length..], target.Id);
                length += 4;
            }
            else
            {
                length += WriteVarUInt32(span[length..], (uint)index);
            }

            _lastState = target;
            _lastStateIndex = index;
        }

        if (argumentHash is { } hash)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[length..], hash);
            length += 4;
        }

        pending.Advance(length);
        return pending;
    }

    private static int WriteVarUInt32(Span<byte> destination, uint value)
    {
        var length = 0;
        while (value >= 0x80)
        {
            destination[length++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[length++] = (byte)value;
        return length;
    }

    private static uint ReadVarUInt32(ReadOnlySpan<byte> input, ref int position)
    {
        uint value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var next = input[position++];
            value |= (uint)(next & 0x7F) << shift;
            if (next < 0x80)
            {
                return value;
            }
        }

        throw new FormatException("A recorded integer is longer than five bytes.");
    }

    private enum AccessLogState : byte
    {
        Recording,
        Closed,
        Discarding,
    }

    // A recorded read with its value and argument hash, which later references return.
    private struct RecordedRead
    {
        public int Key;
        public ReadOnlyMemory<byte> Value;
        public int? Hash;
        public object? Instance;
        public bool HasInstance;
    }

    // The latest read of a state and an operation which this attempt recorded with its value, and its argument hash.
    private abstract class LiveRead(JournaledStateAccessTarget target, JournaledStateOperation operation)
    {
        public JournaledStateAccessTarget Target { get; } = target;

        public JournaledStateOperation Operation { get; } = operation;

        public int? Hash { get; protected set; }
    }

    private sealed class LiveRead<TArgument, TResult>(JournaledStateAccessTarget target, JournaledStateOperation operation) : LiveRead(target, operation)
    {
        private TArgument _argument = default!;
        private TResult _result = default!;
        private long _stamp;

        /// <summary>
        /// Gets whether reads of these types can repeat: by comparing their results, or by their state's stamp and their
        /// argument. A read without a result, such as a check which throws, records nothing to refer to.
        /// </summary>
        public static bool CanRepeat { get; } =
            typeof(TResult) != typeof(JournaledStateAccessNoResult)
            && (ExactEquality<TResult>.IsSupported
                || typeof(TArgument) == typeof(JournaledStateAccessNoArgument)
                || ExactEquality<TArgument>.IsSupported);

        public void Set(TArgument argument, TResult result, int? hash)
        {
            _argument = argument;
            _result = result;
            _stamp = Target.Stamp;
            Hash = hash;
        }

        // A read returns this read's outcome when its result is equal, or, for results which don't compare exactly, when the
        // state hasn't changed and the read's argument is equal.
        public bool Repeats(TArgument argument, TResult result) => ExactEquality<TResult>.IsSupported
            ? EqualityComparer<TResult>.Default.Equals(_result, result)
            : _stamp == Target.Stamp
                && (typeof(TArgument) == typeof(JournaledStateAccessNoArgument) || EqualityComparer<TArgument>.Default.Equals(_argument, argument));
    }
}

/// <summary>
/// Describes whether the default equality of <typeparamref name="T"/> holds exactly when two values are indistinguishable,
/// so a value equal to a recorded one can stand for it.
/// </summary>
/// <remarks>
/// Primitives other than floating-point numbers, strings (ordinally), enumerations, <see cref="Guid"/>,
/// <see cref="TimeSpan"/>, and nullable values and pairs of these compare exactly. Floating-point numbers, whose equality
/// equates zeroes of either sign, and decimals and dates, whose equality ignores scale, kind or offset, don't.
/// </remarks>
internal static class ExactEquality<T>
{
    public static bool IsSupported { get; } = Compute(typeof(T));

    private static bool Compute(Type type)
    {
        if (type.IsEnum
            || type == typeof(bool) || type == typeof(char) || type == typeof(string)
            || type == typeof(sbyte) || type == typeof(byte) || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
            || type == typeof(nint) || type == typeof(nuint) || type == typeof(Guid) || type == typeof(TimeSpan))
        {
            return true;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() is var definition
            && (definition == typeof(Nullable<>) || definition == typeof(ValueTuple<,>)))
        {
            foreach (var argument in type.GetGenericArguments())
            {
                if (!Compute(argument))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }
}
