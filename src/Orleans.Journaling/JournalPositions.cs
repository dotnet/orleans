using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Lets a journaled state answer a read as of an earlier position of its journal, which a context can record instead of
/// the read's value.
/// </summary>
/// <remarks>
/// <para>
/// While <see cref="IsReproducibleFromJournal"/> holds, the journal's entries up to the manager's current position
/// reproduce exactly everything which the state's positional reads observe (<see cref="JournaledStateOperations.IsPositional"/>).
/// States which journal each change as they make it, such as the collections, always reproduce; states which coalesce
/// changes until a write, such as <see cref="DurableValue{T}"/>, don't while a change is pending, so their reads record
/// values until the write journals it.
/// </para>
/// <para>
/// A replica is an isolated instance of the state, with no manager, which replays the state's entries and evaluates a
/// positional read from its encoded argument. Only the manager's history replay uses replicas.
/// </para>
/// </remarks>
internal interface IReplicableState
{
    /// <summary>
    /// Gets whether the journal's committed entries reproduce exactly what the state's positional reads observe now: no
    /// change is pending a write, and the journaled encoding of the content reproduces it.
    /// </summary>
    bool IsReproducibleFromJournal { get; }

    /// <summary>Creates an empty replica of the state.</summary>
    IJournaledStateReplica CreateReplica();
}

/// <summary>An isolated instance of a journaled state, which replays its entries and evaluates positional reads.</summary>
internal interface IJournaledStateReplica : IStateMachine
{
    /// <summary>
    /// Evaluates <paramref name="operation"/> against the replica's content and writes its result, encoded with
    /// <paramref name="serializer"/> as the live read's result type, to <paramref name="result"/>.
    /// </summary>
    /// <param name="operation">A positional operation of the state.</param>
    /// <param name="argument">
    /// The read's argument, encoded with <paramref name="serializer"/> as the type which the live read passes to its context,
    /// or empty when the read takes none.
    /// </param>
    /// <param name="serializer">The serializer which encoded the argument and encodes the result.</param>
    /// <param name="result">The destination of the encoded result.</param>
    void Evaluate(JournaledStateOperation operation, ReadOnlyMemory<byte> argument, Serializer serializer, IBufferWriter<byte> result);
}

/// <summary>Encodes the arguments and results of positional reads.</summary>
internal static class JournaledStateReplicaEncoding
{
    public static T ReadArgument<T>(Serializer serializer, ReadOnlyMemory<byte> argument) => serializer.Deserialize<T>(argument)!;

    public static void WriteResult<T>(Serializer serializer, T value, IBufferWriter<byte> result) =>
        serializer.Serialize<T, IBufferWriter<byte>>(value, result);

    public static InvalidOperationException NotPositional(JournaledStateOperation operation, string name) =>
        new($"Operation '{operation}' of state '{name}' isn't a positional read.");
}

/// <summary>
/// Holds records of positional reads, which refer to positions of its manager's journal.
/// </summary>
/// <remarks>
/// The manager holds a compaction barrier while any holder holds a positional read, and resolves the reads when the history
/// which they need would otherwise become unavailable: at recovery, and before a snapshot which passes the barrier. A state
/// machine registered with the manager implements this.
/// </remarks>
internal interface IJournalPositionHolder
{
    /// <summary>Gets whether any record which the holder holds is a positional read which isn't resolved.</summary>
    /// <remarks>
    /// This derives from the records themselves, staged or not, so the manager releases its barrier exactly when no record
    /// needs the history.
    /// </remarks>
    bool HoldsPositionalReads { get; }

    /// <summary>Returns the holder's unresolved positional reads at or after <paramref name="fromPosition"/>, or <see langword="null"/>.</summary>
    JournalPositionalReads? CollectPositionalReads(long fromPosition);

    /// <summary>
    /// Replaces each read of <paramref name="reads"/> which the manager resolved with its value. Reads which it couldn't
    /// resolve stay positional.
    /// </summary>
    void ApplyResolvedReads(JournalPositionalReads reads);
}

/// <summary>A batch of positional reads, which the manager resolves by replaying its journal's history.</summary>
/// <param name="serializer">The serializer which encoded the reads' arguments and encodes their results.</param>
internal sealed class JournalPositionalReads(Serializer serializer)
{
    private readonly List<Read> _reads = [];
    private readonly ArrayBufferWriter<byte> _results = new();

    public Serializer Serializer { get; } = serializer;

    public int Count => _reads.Count;

    /// <summary>Adds a read and returns its index.</summary>
    /// <param name="position">The journal position as of which the read observed its state.</param>
    /// <param name="stateId">The stable id of the state's name (<see cref="JournaledStateAccessTarget.Id"/>).</param>
    /// <param name="operation">The read's operation.</param>
    /// <param name="argument">The read's encoded argument.</param>
    public int Add(long position, int stateId, JournaledStateOperation operation, ReadOnlyMemory<byte> argument)
    {
        // The argument's owner can reuse its memory, for example a running attempt's pending records which it stages.
        _reads.Add(new() { Position = position, StateId = stateId, Operation = operation, Argument = argument.ToArray(), ResultStart = -1 });
        return _reads.Count - 1;
    }

    /// <summary>Gets the encoded result of the read at <paramref name="index"/>, when the manager resolved it.</summary>
    public bool TryGetResult(int index, out ReadOnlySpan<byte> result)
    {
        var read = _reads[index];
        if (read.ResultStart < 0)
        {
            result = default;
            return false;
        }

        result = _results.WrittenSpan.Slice(read.ResultStart, read.ResultLength);
        return true;
    }

    internal Read this[int index] => _reads[index];

    internal void Resolve(int index, IJournaledStateReplica replica)
    {
        var read = _reads[index];
        var start = _results.WrittenCount;
        try
        {
            replica.Evaluate(read.Operation, read.Argument, Serializer, _results);
        }
        catch
        {
            // Results which follow overwrite the partial one; the read stays unresolved.
            _results.ResetWrittenCount();
            _results.Advance(start);
            throw;
        }

        read.ResultStart = start;
        read.ResultLength = _results.WrittenCount - start;
        _reads[index] = read;
    }

    internal struct Read
    {
        public long Position;
        public int StateId;
        public JournaledStateOperation Operation;
        public ReadOnlyMemory<byte> Argument;
        public int ResultStart;
        public int ResultLength;
    }
}

/// <summary>The manager's compaction barriers, on a reserved stream.</summary>
/// <remarks>
/// <para>
/// A barrier, journaled under its owner's name with the position from which it holds history, keeps every snapshot from
/// passing it until the manager releases it. Changes are journaled at the next write, so a barrier which is set and
/// released between two writes journals nothing.
/// </para>
/// <para>
/// The barriers are the manager's bookkeeping for reads which record positions, on a reserved stream rather than a
/// registered state: a barrier follows such a read, whose record commits with whichever write captures next, so setting
/// or releasing one stages no change of the grain's state, and <see cref="JournaledStateManager.StagedChangeCount"/>
/// doesn't count it.
/// </para>
/// <para>
/// The entries reuse the dictionary commands of <see cref="string"/> keys and values, with positions in invariant decimal.
/// The write format's codec is looked up once, when positions are first requested, so journals whose reads never record
/// positions, and format registrations without that codec, need none: without it, the manager offers no positions.
/// </para>
/// </remarks>
internal sealed class JournalBarrierState(Func<IDurableDictionaryCommandCodec<string, string>?> tryGetCodec) : IStateMachine, IDurableDictionaryCommandHandler<string, string>
{
    /// <summary>The reserved stream id of the journal's barriers.</summary>
    public const uint Id = 2u;

    private readonly Func<IDurableDictionaryCommandCodec<string, string>?> _tryGetCodec = tryGetCodec;
    private readonly Dictionary<string, long> _barriers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
    private IDurableDictionaryCommandCodec<string, string>? _codec;
    private bool _hasLookedUpCodec;

    /// <summary>Gets whether any owner holds a barrier.</summary>
    public bool HasBarriers => _barriers.Count > 0;

    /// <summary>Gets whether the write format has a codec for barriers, so the journal can hold them.</summary>
    public bool CanHoldBarriers => Codec is not null;

    private IDurableDictionaryCommandCodec<string, string>? Codec
    {
        get
        {
            if (!_hasLookedUpCodec)
            {
                _codec = _tryGetCodec();
                _hasLookedUpCodec = true;
            }

            return _codec;
        }
    }

    // Barriers are set only while positions are offered, which requires the codec, or replayed, which required it.
    private IDurableDictionaryCommandCodec<string, string> RequiredCodec
    {
        get
        {
            Debug.Assert(Codec is not null, "The journal holds barriers only when the write format has their codec.");
            return Codec!;
        }
    }

    /// <summary>Holds a barrier for <paramref name="owner"/> from <paramref name="position"/>, unless it holds one.</summary>
    public void Set(string owner, long position)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        if (_barriers.TryAdd(owner, position))
        {
            _changed.Add(owner);
        }
    }

    /// <summary>Releases every barrier.</summary>
    public void RemoveAll()
    {
        foreach (var owner in _barriers.Keys)
        {
            _changed.Add(owner);
        }

        _barriers.Clear();
    }

    /// <summary>Clears the barriers, as a deletion of the journal does.</summary>
    public void ResetVolatileState()
    {
        _barriers.Clear();
        _changed.Clear();
    }

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context)
    {
        var codec = Codec is { } writeCodec
            ? context.GetRequiredCommandCodec(entry.FormatKey, writeCodec)
            : JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, string>>(context.ServiceProvider, entry.FormatKey);
        codec.Apply(entry.Reader, this);
    }

    void IStateMachine.Reset(JournalStreamWriter writer) => ResetVolatileState();

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer)
    {
        foreach (var owner in _changed)
        {
            if (_barriers.TryGetValue(owner, out var position))
            {
                RequiredCodec.WriteSet(owner, Format(position), writer);
            }
            else
            {
                RequiredCodec.WriteRemove(owner, writer);
            }
        }

        _changed.Clear();
    }

    // Resolution releases every barrier before a snapshot, so a snapshot journals barriers only when an owner retakes one.
    void IStateMachine.WriteSnapshot(JournalStreamWriter writer)
    {
        if (_barriers.Count > 0)
        {
            RequiredCodec.WriteSnapshot([.. _barriers.Select(static pair => KeyValuePair.Create(pair.Key, Format(pair.Value)))], writer);
        }

        _changed.Clear();
    }

    void IDurableDictionaryCommandHandler<string, string>.ApplySet(string key, string value) => _barriers[key] = Parse(value);

    void IDurableDictionaryCommandHandler<string, string>.ApplyRemove(string key) => _barriers.Remove(key);

    void IDurableDictionaryCommandHandler<string, string>.ApplyClear() => _barriers.Clear();

    void IDurableDictionaryCommandHandler<string, string>.Reset(int capacityHint) => _barriers.Clear();

    private static string Format(long position) => position.ToString(CultureInfo.InvariantCulture);

    private static long Parse(string value) => long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
}

/// <summary>A registered state which supports positional reads, under its journal name.</summary>
internal sealed record ReplicableStateEntry(string Name, IReplicableState State);

/// <summary>A positional read which a history replay couldn't resolve.</summary>
internal sealed record UnresolvedPositionalRead(long Position, int StateId, JournaledStateOperation Operation, string Reason, Exception? Exception);

/// <summary>
/// Replays a journal's history into replicas of the manager's replicable states, and resolves each positional read as of
/// its position.
/// </summary>
/// <remarks>
/// It counts positions as the manager does, from the start of storage's content: before the format replays each entry, the
/// reads at the position which precedes it are evaluated. A read which can't be resolved, because its position passed
/// without being reached, its state has no replica, or the replica fails to evaluate it, stays unresolved and is reported
/// in <see cref="Unresolved"/>.
/// </remarks>
internal sealed class JournalHistoryReplay : IJournalStorageConsumer, IJournalReplayTarget
{
    private readonly JournaledStateManager _manager;
    private readonly List<(long Position, JournalPositionalReads Batch, int Index)> _reads = [];
    private readonly Dictionary<int, IJournaledStateReplica> _replicasById = [];
    private readonly Dictionary<string, IJournaledStateReplica> _replicasByName = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, IStateMachine> _streams = [];
    private readonly StreamDirectory _directory;
    private readonly Ignored _ignored = new();
    private List<UnresolvedPositionalRead>? _unresolved;
    private int _next;

    /// <param name="manager">The manager whose journal's history is replayed.</param>
    /// <param name="states">The manager's replicable states by their names' stable ids; <see langword="null"/> marks ids which two names share.</param>
    /// <param name="directoryCodec">The codec of the manager's stream directory, which binds names to stream ids.</param>
    public JournalHistoryReplay(
        JournaledStateManager manager,
        IReadOnlyDictionary<int, ReplicableStateEntry?> states,
        IDurableDictionaryCommandCodec<string, uint> directoryCodec)
    {
        _manager = manager;
        _directory = new(this, directoryCodec);

        // Every replicable state replays, since reads collected later can name any of them.
        foreach (var (id, entry) in states)
        {
            if (entry is not null)
            {
                var replica = entry.State.CreateReplica();
                replica.Reset(default);
                _replicasById[id] = replica;
                _replicasByName[entry.Name] = replica;
            }
        }
    }

    /// <summary>Gets the logical position after the entries replayed so far.</summary>
    public long Position { get; private set; }

    /// <summary>Gets the reads which the replay couldn't resolve, with the reason, or <see langword="null"/>.</summary>
    public IReadOnlyList<UnresolvedPositionalRead>? Unresolved => _unresolved;

    string IJournalReplayTarget.WriteJournalFormatKey => _manager.WriteJournalFormatKey;

    IServiceProvider IJournalReplayTarget.ServiceProvider => _manager.ServiceProvider;

    /// <summary>Adds reads to resolve, whose positions are at or after the position reached so far.</summary>
    public void Add(JournalPositionalReads batch)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            _reads.Add((batch[i].Position, batch, i));
        }

        _reads.Sort(_next, _reads.Count - _next, ReadOrder.Instance);
    }

    /// <summary>Forgets the reads which the replay hasn't reached, so a later batch can supply them again.</summary>
    public void DropPending() => _reads.RemoveRange(_next, _reads.Count - _next);

    /// <summary>Reports the reads which the replay hasn't reached as unresolved: the history ends before their positions.</summary>
    public void GiveUpPending()
    {
        while (_next < _reads.Count)
        {
            var (_, batch, index) = _reads[_next++];
            Fail(batch, index, "the journal's history ends before its position", exception: null);
        }
    }

    void IJournalStorageConsumer.Read(JournalBufferReader buffer, IJournalMetadata? metadata)
    {
        if (buffer.Length > 0)
        {
            _manager.Replay(buffer, _manager.GetStoredJournalFormatKey(metadata), this);
        }
    }

    /// <summary>
    /// Replays <paramref name="entries"/>, whole entries in the format of <paramref name="formatKey"/> which follow those
    /// replayed so far: entries which recovery read, or entries which the manager committed but hasn't written.
    /// </summary>
    public void Replay(ArcBuffer entries, string formatKey)
    {
        if (entries.Length == 0)
        {
            return;
        }

        using var buffer = new ArcBufferWriter();
        buffer.AppendPinned(entries);
        _manager.Replay(new JournalBufferReader(buffer.Reader, isCompleted: true), formatKey, this);
    }

    /// <summary>Resolves the reads at the position reached, after the last replayed entry.</summary>
    public void ResolveCurrent() => ResolveThrough(Position);

    IStateMachine IJournalReplayTarget.ResolveStateMachine(JournalStreamId streamId)
    {
        ResolveThrough(Position);
        Position++;
        return streamId.Value is 0u ? _directory : _streams.TryGetValue(streamId.Value, out var state) ? state : _ignored;
    }

    // Resolves the reads at the current position, and gives up on reads whose positions passed.
    private void ResolveThrough(long position)
    {
        while (_next < _reads.Count && _reads[_next].Position <= position)
        {
            var (readPosition, batch, index) = _reads[_next++];
            if (readPosition != position)
            {
                Fail(batch, index, "the journal's history passed its position without reaching it", exception: null);
            }
            else if (!_replicasById.TryGetValue(batch[index].StateId, out var replica))
            {
                Fail(batch, index, "no registered state with that id replays positional reads, or two registered names share the id", exception: null);
            }
            else
            {
                try
                {
                    batch.Resolve(index, replica);
                }
                catch (Exception exception)
                {
                    Fail(batch, index, "the state's replica failed to evaluate it", exception);
                }
            }
        }
    }

    // The read stays unresolved, so its record keeps holding the history: its replay stalls rather than observing another value.
    private void Fail(JournalPositionalReads batch, int index, string reason, Exception? exception)
    {
        var read = batch[index];
        (_unresolved ??= []).Add(new(read.Position, read.StateId, read.Operation, reason, exception));
    }

    private void Bind(string name, uint id)
    {
        if (_replicasByName.TryGetValue(name, out var replica))
        {
            _streams[id] = replica;
            replica.Reset(default);
        }
    }

    private void Unbind(string name)
    {
        if (_replicasByName.TryGetValue(name, out var replica))
        {
            foreach (var (id, state) in _streams)
            {
                if (ReferenceEquals(state, replica))
                {
                    _streams.Remove(id);
                    break;
                }
            }
        }
    }

    private sealed class ReadOrder : IComparer<(long Position, JournalPositionalReads Batch, int Index)>
    {
        public static ReadOrder Instance { get; } = new();

        public int Compare((long Position, JournalPositionalReads Batch, int Index) x, (long Position, JournalPositionalReads Batch, int Index) y) =>
            x.Position.CompareTo(y.Position);
    }

    // The stream directory, which binds replicas to their states' stream ids.
    private sealed class StreamDirectory(JournalHistoryReplay owner, IDurableDictionaryCommandCodec<string, uint> codec)
        : IStateMachine, IDurableDictionaryCommandHandler<string, uint>
    {
        private readonly Dictionary<string, uint> _ids = new(StringComparer.Ordinal);

        void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
            context.GetRequiredCommandCodec(entry.FormatKey, codec).Apply(entry.Reader, this);

        void IStateMachine.Reset(JournalStreamWriter writer) => _ids.Clear();

        void IStateMachine.WritePendingEntries(JournalStreamWriter writer) => throw new NotSupportedException();

        void IStateMachine.WriteSnapshot(JournalStreamWriter writer) => throw new NotSupportedException();

        void IDurableDictionaryCommandHandler<string, uint>.ApplySet(string key, uint value)
        {
            _ids[key] = value;
            owner.Bind(key, value);
        }

        void IDurableDictionaryCommandHandler<string, uint>.ApplyRemove(string key)
        {
            _ids.Remove(key);
            owner.Unbind(key);
        }

        void IDurableDictionaryCommandHandler<string, uint>.ApplyClear() => Clear();

        void IDurableDictionaryCommandHandler<string, uint>.Reset(int capacityHint) => Clear();

        private void Clear()
        {
            foreach (var name in _ids.Keys)
            {
                owner.Unbind(name);
            }

            _ids.Clear();
        }
    }

    // Entries of states which no read names, which only count.
    private sealed class Ignored : IStateMachine
    {
        void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) { }

        void IStateMachine.Reset(JournalStreamWriter writer) { }

        void IStateMachine.WritePendingEntries(JournalStreamWriter writer) => throw new NotSupportedException();

        void IStateMachine.WriteSnapshot(JournalStreamWriter writer) => throw new NotSupportedException();
    }
}
