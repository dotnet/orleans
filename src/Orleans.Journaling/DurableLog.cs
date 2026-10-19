using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

// Every change commits a journal entry when it is made: an append, trim, clear, seal, epoch, or draft commit. An open
// draft stages nothing until it commits, so the log reports each change it stages.
[DebuggerDisplay("Count = {Count}, NextSequence = {NextSequence}")]
internal sealed class DurableLog<T> : IDurableLog<T>, IStateMachine, IReportsStagedChanges, IDurableLogCommandHandler<T>
{
    private readonly IDurableLogCommandCodec<T> _codec;
    private readonly JournaledStateManager? _manager;
    private readonly List<T> _items = [];
    private readonly JournaledStateAccessTarget _accessTarget;
    private JournalStreamWriter _writer;

    // Identifies the history. Zero until the first change, so a log without an epoch has no entries and is not sealed.
    private long _epoch;
    private long _firstSequence;

    // The first sequence after the most recent clear. Readers positioned before it observed entries which the clear replaced.
    private long _resetSequence;
    private bool _isSealed;

    // The log's boundaries when the most recent write was captured, and when the most recent acknowledged write was captured.
    private Boundary _captured;
    private Boundary _committed;

    // The drafts whose committed entries begin at or after the first retained sequence, in sequence order. Allocated by the first commit.
    private List<DraftCommitRecord>? _draftCommits;

    // Numbers this instance's draft commit records. A boundary's count shows which commit records a write captured, which an
    // empty commit's entry range cannot show.
    private long _draftCommitCount;

    // Allocated by the first draft.
    private DraftTable? _drafts;

    public DurableLog(
        [ServiceKey] string key,
        IJournaledStateManager manager,
        JournaledStateManagerShared shared,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = JournalFormatServices.GetRequiredCommandCodec<IDurableLogCommandCodec<T>>(serviceProvider, shared.JournalFormatKey);
        _manager = manager as JournaledStateManager;
        _accessTarget = new(key, manager);
        manager.RegisterStateMachine(key, this);
    }

    internal DurableLog(string key, IJournaledStateManager manager, IDurableLogCommandCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        _manager = manager as JournaledStateManager;
        _accessTarget = new(key, manager);
        manager.RegisterStateMachine(key, this);
    }

    public int Count => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Read(_accessTarget, JournaledStateOperation.LogCount, this, static self => self._items.Count)
        : _items.Count;

    public long Epoch => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Read(_accessTarget, JournaledStateOperation.LogEpoch, this, static self => self._epoch)
        : _epoch;

    public long FirstSequence => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Read(_accessTarget, JournaledStateOperation.LogFirstSequence, this, static self => self._firstSequence)
        : _firstSequence;

    public long NextSequence => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Read(_accessTarget, JournaledStateOperation.LogNextSequence, this, static self => self.NextSequenceCore)
        : NextSequenceCore;

    private long NextSequenceCore => _firstSequence + _items.Count;

    public long CommittedSequence => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Read(_accessTarget, JournaledStateOperation.LogCommittedSequence, this, static self => self._committed.NextSequence)
        : _committed.NextSequence;

    public bool IsSealed => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Read(_accessTarget, JournaledStateOperation.LogIsSealed, this, static self => self._isSealed)
        : _isSealed;

    public T this[long sequence] => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Read(_accessTarget, JournaledStateOperation.LogGet, sequence, (Self: this, Sequence: sequence), static state => state.Self.GetCore(state.Sequence))
        : GetCore(sequence);

    private T GetCore(long sequence) => TryGetIndex(sequence, out var index)
        ? _items[index]
        : throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "The log does not retain an entry with this sequence number.");

    private Boundary Current => new(NextSequenceCore, _firstSequence, _resetSequence, _isSealed, _draftCommitCount);

    public bool TryGet(long sequence, [MaybeNullWhen(false)] out T item)
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            (var found, item) = context.Read(
                _accessTarget,
                JournaledStateOperation.LogTryGet,
                sequence,
                (Self: this, Sequence: sequence),
                static state => state.Self.TryGetIndex(state.Sequence, out var index) ? (true, state.Self._items[index]) : (false, default(T)!));
            return found;
        }

        if (TryGetIndex(sequence, out var index))
        {
            item = _items[index];
            return true;
        }

        item = default;
        return false;
    }

    public long Append(T item) => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Write(_accessTarget, JournaledStateOperation.LogAppend, item, (Self: this, Item: item), static state => state.Self.AppendCore(state.Item))
        : AppendCore(item);

    private long AppendCore(T item)
    {
        ThrowIfSealed();
        var sequence = NextSequenceCore;
        EnsureEpoch();
        _codec.WriteAppend(new ReadOnlySpan<T>(in item), GetWriter());
        _items.Add(item);
        SignalReaders(DurableWatchMode.Staged);
        return sequence;
    }

    public void TrimBefore(long sequence)
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            context.Write(_accessTarget, JournaledStateOperation.LogTrimBefore, sequence, (Self: this, Sequence: sequence), static state => state.Self.TrimBeforeCore(state.Sequence));
            return;
        }

        TrimBeforeCore(sequence);
    }

    private void TrimBeforeCore(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sequence, NextSequenceCore);
        if (sequence <= _firstSequence)
        {
            return;
        }

        Debug.Assert(_epoch != 0, "A log with entries has an epoch.");
        _codec.WriteTrimBefore(sequence, GetWriter());
        ApplyTrimBefore(sequence);
        SignalReaders(DurableWatchMode.Staged);
    }

    public void Clear()
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            context.Write(_accessTarget, JournaledStateOperation.LogClear, this, static self => self.ClearCore());
            return;
        }

        ClearCore();
    }

    private void ClearCore()
    {
        ThrowIfSealed();
        EnsureEpoch();
        _codec.WriteClear(GetWriter());
        ApplyClear();
        SignalReaders(DurableWatchMode.Staged);
    }

    public void Seal()
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            context.Write(_accessTarget, JournaledStateOperation.LogSeal, this, static self => self.SealCore());
        }
        else
        {
            SealCore();
        }

        // Every successful attempt closes its own drafts, including when the context replays the durable seal.
        if (_drafts is { } drafts)
        {
            while (drafts.Open.Count > 0)
            {
                CloseDraft(drafts.Open[^1], DraftOutcome.Abandoned(LogDraftAbandonReason.Sealed, message: null));
            }
        }
    }

    private void SealCore()
    {
        if (_isSealed)
        {
            return;
        }

        EnsureEpoch();
        _codec.WriteSeal(GetWriter());
        _isSealed = true;
    }

    public IDurableLogDraft<T> BeginDraft()
    {
        // A draft can't begin on a sealed log, so the check reads the state through the context, as other reads do.
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
            ? context.Read(_accessTarget, JournaledStateOperation.LogIsSealed, this, static self => self._isSealed)
            : _isSealed)
        {
            ThrowIfSealed();
        }

        var drafts = _drafts ??= new DraftTable();
        var draft = new Draft(this, new LogDraftId(drafts.InstanceId, ++drafts.LastNumber));
        drafts.Open.Add(draft);
        return draft;
    }

    public IAsyncEnumerable<LogEvent<T>> ReadAsync(
        long fromSequence = 0,
        long epoch = 0,
        DurableWatchMode mode = DurableWatchMode.Committed,
        LogDraftId? resumeDraftId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(epoch);
        DurableStateObservation.ValidateMode(mode);
        return DurableStateObservation.WatchAsync(
            DurableStateObservation.GetRequiredManager(_manager),
            (Log: this, FromSequence: fromSequence, Epoch: epoch, Mode: mode, ResumeDraftId: resumeDraftId),
            static (hub, arg) => arg.Log.CreateReader(hub, arg.FromSequence, arg.Epoch, arg.Mode, arg.ResumeDraftId),
            cancellationToken);
    }

    public IEnumerator<T> GetEnumerator() => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? ((IEnumerable<T>)context.Read(_accessTarget, JournaledStateOperation.LogSnapshot, this, static self => self._items.ToArray())).GetEnumerator()
        : _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // A replayed commit returns its recorded sequence without committing again, so its outcome is the retained commit record
    // of its draft. A trimmed record precedes every retained one, so the latest record's durability implies its durability.
    private DraftOutcome GetCommittedOutcome(LogDraftId draftId, long sequence, int count)
    {
        if (_draftCommits is { } commits)
        {
            for (var i = commits.Count - 1; i >= 0; i--)
            {
                if (commits[i].Commit.DraftId == draftId)
                {
                    return DraftOutcome.Committed(_epoch, commits[i]);
                }
            }
        }

        return DraftOutcome.Committed(_epoch, new(new(draftId, sequence, count), _draftCommitCount));
    }

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        _items.Clear();
        _epoch = 0;
        _firstSequence = 0;
        _resetSequence = 0;
        _isSealed = false;
        _captured = _committed = default;
        _draftCommits?.Clear();
        _writer = writer;
        if (_drafts is { } drafts)
        {
            while (drafts.Open.Count > 0)
            {
                CloseDraft(drafts.Open[^1], DraftOutcome.Abandoned(LogDraftAbandonReason.JournalDeleted, message: null));
            }
        }

        SignalAllReaders();
    }

    void IStateMachine.OnRecoveryCompleted()
    {
        _captured = _committed = Current;
        SignalAllReaders();
    }

    // Entries and commands are journaled when they are made. Capturing records which of them this write makes durable.
    void IStateMachine.WritePendingEntries(JournalStreamWriter writer) => _captured = Current;

    void IStateMachine.WriteSnapshot(JournalStreamWriter writer)
    {
        _codec.WriteSnapshot(
            _epoch,
            _firstSequence,
            _resetSequence,
            _isSealed,
            _items,
            _draftCommits is { Count: > 0 } commits ? commits.ConvertAll(static record => record.Commit) : [],
            writer);
        _captured = Current;
    }

    // Every write captures this log before its acknowledgement. The manager publishes the write's watermark after this
    // callback, which wakes readers.
    void IStateMachine.OnWriteCompleted() => _committed = _captured;

    void IDurableLogCommandHandler<T>.ApplyEpoch(long epoch)
    {
        if (epoch <= 0 || _epoch != 0)
        {
            throw new InvalidOperationException(
                $"Malformed durable log entry: cannot begin a history with epoch {epoch} when the current epoch is {_epoch}.");
        }

        Debug.Assert(NextSequenceCore == 0 && !_isSealed, "A log without an epoch has no entries and is not sealed.");
        _epoch = epoch;
    }

    void IDurableLogCommandHandler<T>.ApplyAppend(T item)
    {
        ThrowIfMalformedChange("append");
        _items.Add(item);
    }

    void IDurableLogCommandHandler<T>.ApplyDraftCommit(LogDraftCommit commit)
    {
        var previousEnd = _draftCommits is { Count: > 0 } commits ? commits[^1].Commit.Sequence + commits[^1].Commit.Count : _firstSequence;
        if (commit.Count < 0 || commit.Sequence < previousEnd || commit.Sequence + commit.Count > NextSequenceCore)
        {
            throw new InvalidOperationException(
                $"Malformed durable log entry: a draft commit of {commit.Count} entries at sequence {commit.Sequence} is outside "
                + $"the uncommitted entries from sequence {previousEnd} to {NextSequenceCore}.");
        }

        (_draftCommits ??= []).Add(new(commit, ++_draftCommitCount));
    }

    void IDurableLogCommandHandler<T>.ApplyTrimBefore(long sequence)
    {
        if (sequence < 0 || sequence > NextSequenceCore)
        {
            throw new InvalidOperationException(
                $"Malformed durable log entry: cannot trim before sequence {sequence} when the next sequence is {NextSequenceCore}.");
        }

        ApplyTrimBefore(sequence);
    }

    void IDurableLogCommandHandler<T>.ApplyClear()
    {
        ThrowIfMalformedChange("clear");
        ApplyClear();
    }

    void IDurableLogCommandHandler<T>.ApplySeal()
    {
        if (_epoch == 0)
        {
            throw new InvalidOperationException("Malformed durable log entry: a seal command precedes the log's epoch.");
        }

        _isSealed = true;
    }

    void IDurableLogCommandHandler<T>.Reset(long epoch, long firstSequence, long resetSequence, int capacityHint)
    {
        if (epoch < 0 || resetSequence < 0 || firstSequence < resetSequence || (epoch == 0 && firstSequence != 0))
        {
            throw new InvalidOperationException(
                $"Malformed durable log snapshot: epoch {epoch}, first sequence {firstSequence}, and reset sequence {resetSequence} are inconsistent.");
        }

        _items.Clear();
        _items.EnsureCapacity(capacityHint);
        _draftCommits?.Clear();
        _epoch = epoch;
        _firstSequence = firstSequence;
        _resetSequence = resetSequence;
        _isSealed = false;
    }

    // Replay enforces the invariants which the writer enforces: a change belongs to a history, and a sealed log accepts no
    // entries or clears.
    private void ThrowIfMalformedChange(string command)
    {
        if (_epoch == 0 || _isSealed)
        {
            throw new InvalidOperationException(
                $"Malformed durable log entry: {command} command for a log which {(_isSealed ? "is sealed" : "has no epoch")}.");
        }
    }

    // A history begins with its epoch, so that a reader position from another history is detected in any activation.
    private void EnsureEpoch()
    {
        if (_epoch == 0)
        {
            var epoch = Random.Shared.NextInt64(1, long.MaxValue);
            _codec.WriteEpoch(epoch, GetWriter());
            _epoch = epoch;
        }
    }

    // Journals a draft's entries and outcome as one journal entry.
    private DraftOutcome CommitDraft(LogDraftId draftId, ReadOnlySpan<T> items)
    {
        Debug.Assert(!_isSealed, "Sealing closes open drafts.");
        var sequence = NextSequenceCore;
        EnsureEpoch();
        _codec.WriteDraftCommit(draftId, sequence, items, GetWriter());
        _items.AddRange(items);
        var record = new DraftCommitRecord(new(draftId, sequence, items.Length), ++_draftCommitCount);
        (_draftCommits ??= []).Add(record);
        if (!items.IsEmpty)
        {
            SignalReaders(DurableWatchMode.Staged);
        }

        return DraftOutcome.Committed(_epoch, record);
    }

    // The clear consumes one sequence number, so a reader's position shows whether it observed the replaced entries.
    private void ApplyClear()
    {
        var sequence = NextSequenceCore + 1;
        _items.Clear();
        _draftCommits?.Clear();
        _firstSequence = sequence;
        _resetSequence = sequence;
    }

    private void ThrowIfSealed()
    {
        if (_isSealed)
        {
            throw new InvalidOperationException("The log is sealed. A sealed log accepts no entries, clears, or drafts.");
        }
    }

    private void ApplyTrimBefore(long sequence)
    {
        if (sequence <= _firstSequence)
        {
            return;
        }

        _items.RemoveRange(0, checked((int)(sequence - _firstSequence)));
        _firstSequence = sequence;
        if (_draftCommits is { } commits)
        {
            var retained = commits.FindIndex(record => record.Commit.Sequence >= sequence);
            commits.RemoveRange(0, retained < 0 ? commits.Count : retained);
        }
    }

    private bool TryGetIndex(long sequence, out int index)
    {
        if (sequence >= _firstSequence && sequence < NextSequenceCore)
        {
            index = (int)(sequence - _firstSequence);
            return true;
        }

        index = -1;
        return false;
    }

    private JournalStreamWriter GetWriter()
    {
        Debug.Assert(_writer.IsInitialized);
        return _writer;
    }

    private DurableChangeStatus GetEntryStatus(long sequence) =>
        sequence < _committed.NextSequence ? DurableChangeStatus.Committed : DurableChangeStatus.Staged;

    private void SignalReaders(DurableWatchMode visibility)
    {
        if (DurableStateObservation.TryGetTopic(_manager, this, out var topic))
        {
            topic.Signal(visibility);
        }
    }

    private void SignalAllReaders()
    {
        if (DurableStateObservation.TryGetTopic(_manager, this, out var topic))
        {
            topic.SignalAll();
        }
    }

    private void CloseDraft(Draft draft, DraftOutcome outcome)
    {
        Debug.Assert(draft.IsOpen);
        var drafts = _drafts!;
        var removed = drafts.Open.Remove(draft);
        Debug.Assert(removed);
        draft.Close(outcome);
        if (!outcome.IsCommitted)
        {
            drafts.RecordAbandonment(draft.Id.Number, outcome);
        }

        SignalReaders(DurableWatchMode.Drafts);
    }

    // Follows the draft a reconnecting reader was viewing: its items while it is open, otherwise its outcome. The log is the
    // authority for commits, which it retains with their entries across activations. Abandonment is volatile, like draft items.
    private FollowedDraft ResumeDraft(LogDraftId id)
    {
        var drafts = _drafts is { } table && table.InstanceId == id.InstanceId ? table : null;
        if (drafts is not null)
        {
            foreach (var draft in drafts.Open)
            {
                if (draft.Id == id)
                {
                    return new(draft);
                }
            }
        }

        if (_draftCommits is { } commits)
        {
            for (var i = commits.Count - 1; i >= 0; i--)
            {
                if (commits[i].Commit.DraftId == id)
                {
                    return new(id, DraftOutcome.Committed(_epoch, commits[i]));
                }
            }
        }

        if (drafts is null)
        {
            return new(id, DraftOutcome.Abandoned(LogDraftAbandonReason.PreviousActivation, message: null));
        }

        return new(id, drafts.TryGetAbandonment(id.Number, out var outcome)
            ? outcome
            : DraftOutcome.Abandoned(LogDraftAbandonReason.Unknown, message: null));
    }

    private Reader CreateReader(JournaledStateObservers hub, long fromSequence, long epoch, DurableWatchMode mode, LogDraftId? resumeDraftId)
    {
        var reader = new Reader(hub, this, mode, fromSequence, epoch is 0 ? _epoch : epoch, catchUpSequence: _committed.NextSequence);
        reader.KnownFirstSequence = Math.Min(fromSequence, GetVisibleFirstSequence(reader));
        if (reader.IncludesDrafts && resumeDraftId is { } draftId)
        {
            reader.FollowedDrafts = [ResumeDraft(draftId)];
        }

        return reader;
    }

    // Readers without staged changes observe a trim once it is durable.
    private long GetVisibleFirstSequence(Reader reader) => reader.IncludesStaged ? _firstSequence : _committed.FirstSequence;

    // Computes the reader's next event from the current log state. Called between yields, so each call observes mutations
    // made while the reader was suspended. The checks run in order of precedence:
    // 1. The outcomes of drafts the reader follows, so that the reader learns how they closed before a history change. A commit
    //    whose history was deleted is reported as abandoned, since its entries no longer exist.
    // 2. A history change: another epoch, a position past the end, or a clear. Each repositions the reader with Reset.
    // 3. A trim, then the committed confirmation of a staged trim.
    // 4. CaughtUp, then a watermark which confirms delivered staged entries, then the next entry.
    // 5. Sealed, once every entry has been delivered and confirmed.
    // 6. Draft items.
    private bool TryGetNextEvent(Reader reader, out LogEvent<T> logEvent)
    {
        if (reader.Epoch == 0)
        {
            // A reader which has observed no history adopts the log's history, which begins at sequence zero.
            reader.Epoch = _epoch;
        }

        if (reader.FollowedDrafts is { Count: > 0 } followed && TryGetDraftOutcomeEvent(reader, followed, out logEvent))
        {
            return true;
        }

        return TryGetResetEvent(reader, out logEvent)
            || TryGetTrimEvent(reader, out logEvent)
            || TryGetEntryEvent(reader, out logEvent)
            || TryGetSealedEvent(reader, out logEvent)
            || (reader.IncludesDrafts && _drafts is { Open.Count: > 0 } drafts && TryGetDraftItemEvent(reader, drafts, out logEvent));
    }

    private bool TryGetResetEvent(Reader reader, out LogEvent<T> logEvent)
    {
        var isOtherHistory = reader.Epoch != _epoch && reader.Epoch != 0;
        reader.Epoch = _epoch;
        if (isOtherHistory)
        {
            // When the history changed during the read, the entries which were committed when the read began no longer exist.
            if (_epoch != reader.CatchUpEpoch)
            {
                reader.CatchUpSequence = 0;
            }

            logEvent = ResetReader(reader, _committed.FirstSequence, DurableChangeStatus.Committed, unconfirmedSequence: -1);
            return true;
        }

        if (reader.Cursor > NextSequenceCore)
        {
            logEvent = ResetReader(reader, _committed.FirstSequence, DurableChangeStatus.Committed, unconfirmedSequence: -1);
            return true;
        }

        var resetSequence = reader.IncludesStaged ? _resetSequence : _committed.ResetSequence;
        if (reader.Cursor < resetSequence)
        {
            if (resetSequence <= _committed.ResetSequence)
            {
                logEvent = ResetReader(reader, _committed.FirstSequence, DurableChangeStatus.Committed, unconfirmedSequence: -1);
                return true;
            }

            // A staged reset reports the staged first sequence. When a trim moved it beyond the clear, the trim's write confirms it.
            logEvent = ResetReader(reader, _firstSequence, DurableChangeStatus.Staged, unconfirmedSequence: resetSequence - 1);
            reader.UnconfirmedTrimSequence = _firstSequence > resetSequence ? _firstSequence : -1;
            return true;
        }

        logEvent = default;
        return false;
    }

    private bool TryGetTrimEvent(Reader reader, out LogEvent<T> logEvent)
    {
        var firstSequence = GetVisibleFirstSequence(reader);
        if (firstSequence > reader.KnownFirstSequence)
        {
            reader.KnownFirstSequence = firstSequence;
            reader.Cursor = Math.Max(reader.Cursor, firstSequence);
            var status = firstSequence <= _committed.FirstSequence ? DurableChangeStatus.Committed : DurableChangeStatus.Staged;
            reader.UnconfirmedTrimSequence = status is DurableChangeStatus.Staged ? firstSequence : -1;
            logEvent = new() { Kind = LogEventKind.Trimmed, Sequence = firstSequence, Status = status };
            return true;
        }

        if (reader.UnconfirmedTrimSequence >= 0 && reader.UnconfirmedTrimSequence <= _committed.FirstSequence)
        {
            logEvent = new() { Kind = LogEventKind.Trimmed, Sequence = reader.UnconfirmedTrimSequence, Status = DurableChangeStatus.Committed };
            reader.UnconfirmedTrimSequence = -1;
            return true;
        }

        logEvent = default;
        return false;
    }

    private bool TryGetEntryEvent(Reader reader, out LogEvent<T> logEvent)
    {
        if (!reader.IsCaughtUp && reader.Cursor >= reader.CatchUpSequence)
        {
            reader.IsCaughtUp = true;
            logEvent = new() { Kind = LogEventKind.CaughtUp, Sequence = reader.Cursor, Status = DurableChangeStatus.Committed };
            return true;
        }

        // An eligible watermark precedes further entries, so a producer which keeps appending cannot defer it indefinitely.
        if (reader.UnconfirmedSequence >= 0 && reader.UnconfirmedSequence < _committed.NextSequence)
        {
            var watermark = Math.Min(_committed.NextSequence, reader.Cursor);
            reader.UnconfirmedSequence = watermark < reader.Cursor ? watermark : -1;
            logEvent = new() { Kind = LogEventKind.Watermark, Sequence = watermark, Status = DurableChangeStatus.Committed };
            return true;
        }

        // A committed reader positioned before an uncommitted trim awaits the trim's write.
        var limit = reader.IncludesStaged ? NextSequenceCore : _committed.NextSequence;
        if (reader.Cursor >= _firstSequence && reader.Cursor < limit)
        {
            var sequence = reader.Cursor++;
            var status = GetEntryStatus(sequence);
            if (status is DurableChangeStatus.Staged && reader.UnconfirmedSequence < 0)
            {
                reader.UnconfirmedSequence = sequence;
            }

            logEvent = new() { Kind = LogEventKind.Entry, Sequence = sequence, Item = _items[(int)(sequence - _firstSequence)], Status = status };
            return true;
        }

        logEvent = default;
        return false;
    }

    private bool TryGetSealedEvent(Reader reader, out LogEvent<T> logEvent)
    {
        if (_committed.IsSealed && reader.Cursor >= NextSequenceCore && reader.UnconfirmedSequence < 0)
        {
            Debug.Assert(reader.IsCaughtUp && _drafts is not { Open.Count: > 0 });
            logEvent = new() { Kind = LogEventKind.Sealed, Sequence = NextSequenceCore, Status = DurableChangeStatus.Committed };
            return true;
        }

        logEvent = default;
        return false;
    }

    private static LogEvent<T> ResetReader(Reader reader, long firstSequence, DurableChangeStatus status, long unconfirmedSequence)
    {
        reader.Cursor = firstSequence;
        reader.KnownFirstSequence = firstSequence;
        reader.UnconfirmedSequence = unconfirmedSequence;
        reader.UnconfirmedTrimSequence = -1;
        return new() { Kind = LogEventKind.Reset, Sequence = firstSequence, Status = status };
    }

    private bool TryGetDraftOutcomeEvent(Reader reader, List<FollowedDraft> followed, out LogEvent<T> logEvent)
    {
        for (var i = 0; i < followed.Count; i++)
        {
            var followedDraft = followed[i];
            if (!followedDraft.TryGetOutcome(out var outcome))
            {
                continue;
            }

            if (outcome.IsCommitted && outcome.Epoch != _epoch)
            {
                // Deleting the journal discarded the committed entries, along with the rest of their history.
                outcome = DraftOutcome.Abandoned(LogDraftAbandonReason.JournalDeleted, message: null);
            }

            if (outcome.IsCommitted)
            {
                // Readers without staged entries learn of a commit once its record, which contains its entries, is durable.
                var isDurable = outcome.CommitIndex <= _committed.DraftCommitCount;
                if (!isDurable && !reader.IncludesStaged)
                {
                    continue;
                }

                followed.RemoveAt(i);
                logEvent = new()
                {
                    Kind = LogEventKind.DraftCommitted,
                    Epoch = outcome.Epoch,
                    Sequence = outcome.Sequence,
                    Count = outcome.Count,
                    DraftId = followedDraft.Id,
                    Status = isDurable ? DurableChangeStatus.Committed : DurableChangeStatus.Staged,
                };
                return true;
            }

            followed.RemoveAt(i);
            logEvent = new()
            {
                Kind = LogEventKind.DraftAbandoned,
                DraftId = followedDraft.Id,
                AbandonReason = outcome.AbandonReason,
                Message = outcome.Message,
                Status = DurableChangeStatus.Draft,
            };
            return true;
        }

        logEvent = default;
        return false;
    }

    private static bool TryGetDraftItemEvent(Reader reader, DraftTable drafts, out LogEvent<T> logEvent)
    {
        var followed = reader.FollowedDrafts ??= [];
        foreach (var draft in drafts.Open)
        {
            if (!IsFollowed(followed, draft))
            {
                followed.Add(new FollowedDraft(draft));
            }
        }

        foreach (var followedDraft in followed)
        {
            if (followedDraft.Draft is { IsOpen: true } draft && followedDraft.Position < draft.Count)
            {
                var position = followedDraft.Position++;
                logEvent = new()
                {
                    Kind = LogEventKind.DraftItem,
                    Sequence = position,
                    Item = draft.GetItem(position),
                    DraftId = draft.Id,
                    Status = DurableChangeStatus.Draft,
                };
                return true;
            }
        }

        logEvent = default;
        return false;

        static bool IsFollowed(List<FollowedDraft> followed, Draft draft)
        {
            foreach (var followedDraft in followed)
            {
                if (ReferenceEquals(followedDraft.Draft, draft))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The log's boundaries at one point: where its entries end and begin, where its most recent clear began, whether it is
    /// sealed, and how many draft commit records it holds.
    /// </summary>
    private readonly record struct Boundary(long NextSequence, long FirstSequence, long ResetSequence, bool IsSealed, long DraftCommitCount);

    /// <summary>
    /// A draft commit retained by the log, and its number among this instance's commit records.
    /// </summary>
    private readonly record struct DraftCommitRecord(LogDraftCommit Commit, long Index);

    /// <summary>
    /// How a draft closed: the range of entries which replaced it, or why it was abandoned.
    /// </summary>
    private readonly record struct DraftOutcome(
        long Epoch,
        long Sequence,
        int Count,
        long CommitIndex,
        LogDraftAbandonReason AbandonReason,
        string? Message)
    {
        public bool IsCommitted => AbandonReason is LogDraftAbandonReason.None;

        public static DraftOutcome Committed(long epoch, DraftCommitRecord record) =>
            new(epoch, record.Commit.Sequence, record.Commit.Count, record.Index, LogDraftAbandonReason.None, Message: null);

        public static DraftOutcome Abandoned(LogDraftAbandonReason reason, string? message) => new(0, 0, 0, 0, reason, message);
    }

    /// <summary>
    /// The open drafts of one log instance and why its most recently abandoned drafts closed.
    /// </summary>
    private sealed class DraftTable
    {
        // Reconnecting readers learn why this many recently abandoned drafts closed.
        private const int MaxRecentAbandonments = 16;

        private readonly Queue<(long Number, DraftOutcome Outcome)> _recentAbandonments = new();

        public Guid InstanceId { get; } = Guid.NewGuid();

        public long LastNumber { get; set; }

        public List<Draft> Open { get; } = [];

        public void RecordAbandonment(long number, DraftOutcome outcome)
        {
            if (_recentAbandonments.Count == MaxRecentAbandonments)
            {
                _recentAbandonments.Dequeue();
            }

            _recentAbandonments.Enqueue((number, outcome));
        }

        public bool TryGetAbandonment(long number, out DraftOutcome outcome)
        {
            foreach (var (abandonedNumber, abandonedOutcome) in _recentAbandonments)
            {
                if (abandonedNumber == number)
                {
                    outcome = abandonedOutcome;
                    return true;
                }
            }

            outcome = default;
            return false;
        }
    }

    private sealed class Draft(DurableLog<T> log, LogDraftId id) : IDurableLogDraft<T>
    {
        private List<T>? _items = [];

        public LogDraftId Id { get; } = id;

        public bool IsOpen => _items is not null;

        public int Count => _items?.Count ?? 0;

        public DraftOutcome Outcome { get; private set; }

        public T GetItem(int position) => _items![position];

        public void Write(T item)
        {
            ThrowIfClosed();
            _items!.Add(item);
            log.SignalReaders(DurableWatchMode.Drafts);
        }

        public long Commit(params ReadOnlySpan<T> entries)
        {
            // Validation and append share one recorded outcome; each attempt closes its own successful draft.
            var sequence = JournaledStateContext.TryGetCurrent(log._accessTarget, out var context)
                ? context.Write(log._accessTarget, JournaledStateOperation.LogCommitDraft, (Self: this, Entries: entries.ToArray()), static state => state.Self.CommitCore(state.Entries))
                : CommitCore(entries);
            log.CloseDraft(this, log.GetCommittedOutcome(Id, sequence, entries.Length));
            return sequence;
        }

        private long CommitCore(ReadOnlySpan<T> entries)
        {
            ThrowIfClosed();
            return log.CommitDraft(Id, entries).Sequence;
        }

        public void Abandon(string? message = null)
        {
            if (IsOpen)
            {
                log.CloseDraft(this, DraftOutcome.Abandoned(LogDraftAbandonReason.Abandoned, message));
            }
        }

        public void Dispose()
        {
            if (IsOpen)
            {
                log.CloseDraft(this, DraftOutcome.Abandoned(LogDraftAbandonReason.Disposed, message: null));
            }
        }

        internal void Close(DraftOutcome outcome)
        {
            Outcome = outcome;
            _items = null;
        }

        private void ThrowIfClosed()
        {
            if (!IsOpen)
            {
                throw new InvalidOperationException(Outcome.AbandonReason switch
                {
                    LogDraftAbandonReason.None => $"Draft {Id} is closed because it was committed.",
                    LogDraftAbandonReason.Abandoned => $"Draft {Id} is closed because it was abandoned.",
                    LogDraftAbandonReason.Disposed => $"Draft {Id} is closed because it was disposed.",
                    _ => $"Draft {Id} was closed by the log because {DescribeLogClosure(Outcome.AbandonReason)}. "
                        + "Check IsOpen before using a draft which the log can close.",
                });
            }
        }

        private static string DescribeLogClosure(LogDraftAbandonReason reason) => reason switch
        {
            LogDraftAbandonReason.JournalDeleted => "the log's journal was deleted",
            LogDraftAbandonReason.Sealed => "the log was sealed",
            _ => reason.ToString(),
        };
    }

    private sealed class Reader(JournaledStateObservers hub, DurableLog<T> log, DurableWatchMode mode, long cursor, long epoch, long catchUpSequence)
        : StateWatcher<LogEvent<T>>(hub, log, mode)
    {
        private bool _isEnded;

        public bool IncludesDrafts => (Mode & DurableWatchMode.Drafts) != 0;

        public long Cursor { get; set; } = cursor;

        // The epoch of the history which the reader's position belongs to, or zero when the reader has observed no history.
        public long Epoch { get; set; } = epoch;

        // The first retained sequence the reader has been told of. A higher first sequence is reported as a trim.
        public long KnownFirstSequence { get; set; }

        // The first sequence delivered as staged which no watermark has confirmed, or -1.
        public long UnconfirmedSequence { get; set; } = -1;

        // The latest staged retention boundary which awaits a committed trim notification.
        public long UnconfirmedTrimSequence { get; set; } = -1;

        // The position after the entries which were committed when the read began, and the epoch of their history.
        public long CatchUpSequence { get; set; } = catchUpSequence;

        public long CatchUpEpoch { get; } = log._epoch;

        public bool IsCaughtUp { get; set; }

        public List<FollowedDraft>? FollowedDrafts { get; set; }

        public override bool TryRead(out LogEvent<T> value)
        {
            Hub.AssertConfined();
            if (_isEnded || !log.TryGetNextEvent(this, out value))
            {
                value = default;
                return false;
            }

            // A commit's range belongs to the commit's history, which can precede the reader's Reset into it.
            if (value.Kind is not LogEventKind.DraftCommitted)
            {
                value = value with { Epoch = Epoch };
            }
            if (value.Kind is LogEventKind.Sealed)
            {
                // Sealed is the stream's final event.
                _isEnded = true;
                Complete(failure: null);
            }

            return true;
        }
    }

    /// <summary>
    /// A draft which a reader follows: an open draft, or the outcome of a draft which closed before the reader resumed it.
    /// </summary>
    private sealed class FollowedDraft
    {
        private readonly DraftOutcome _outcome;

        public FollowedDraft(Draft draft)
        {
            Draft = draft;
            Id = draft.Id;
        }

        public FollowedDraft(LogDraftId id, DraftOutcome outcome)
        {
            Id = id;
            _outcome = outcome;
        }

        public LogDraftId Id { get; }

        public Draft? Draft { get; }

        public int Position { get; set; }

        public bool TryGetOutcome(out DraftOutcome outcome)
        {
            if (Draft is { } draft)
            {
                outcome = draft.Outcome;
                return !draft.IsOpen;
            }

            outcome = _outcome;
            return true;
        }
    }
}
