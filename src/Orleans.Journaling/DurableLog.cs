using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

[DebuggerDisplay("Count = {Count}, NextSequence = {NextSequence}")]
internal sealed class DurableLog<T> : IDurableLog<T>, IStateMachine, IDurableLogCommandHandler<T>
{
    private readonly IDurableLogCommandCodec<T> _codec;
    private readonly JournaledStateManager? _manager;
    private readonly List<T> _items = [];
    private JournalStreamWriter _writer;
    private long _firstSequence;

    // The first sequence after the most recent clear. Readers positioned before it observed history which the clear replaced.
    private long _resetSequence;

    // The next sequence when the most recent write was captured, and when the most recent acknowledged write was captured.
    private long _capturedSequence;
    private long _committedSequence;

    // The first sequence when the most recent write was captured, and when the most recent acknowledged write was captured.
    private long _capturedFirstSequence;
    private long _committedFirstSequence;

    // Incremented when the journal is deleted, which restarts the sequence.
    private int _resetCount;
    private bool _isSealed;
    private bool _isSealCaptured;
    private bool _isSealCommitted;

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
        manager.RegisterStateMachine(key, this);
    }

    internal DurableLog(string key, IJournaledStateManager manager, IDurableLogCommandCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        _manager = manager as JournaledStateManager;
        manager.RegisterStateMachine(key, this);
    }

    public int Count => _items.Count;

    public long FirstSequence => _firstSequence;

    public long NextSequence => _firstSequence + _items.Count;

    public long CommittedSequence => _committedSequence;

    public bool IsSealed => _isSealed;

    public T this[long sequence] => TryGetIndex(sequence, out var index)
        ? _items[index]
        : throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "The log does not retain an entry with this sequence number.");

    public bool TryGet(long sequence, [MaybeNullWhen(false)] out T item)
    {
        if (TryGetIndex(sequence, out var index))
        {
            item = _items[index];
            return true;
        }

        item = default;
        return false;
    }

    public long Append(T item) => AppendRange(new ReadOnlySpan<T>(in item));

    public void TrimBefore(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sequence, NextSequence);
        if (sequence <= _firstSequence)
        {
            return;
        }

        _codec.WriteTrimBefore(sequence, GetWriter());
        ApplyTrimBefore(sequence);
        SignalReaders(DurableWatchMode.Staged);
    }

    public void Clear()
    {
        ThrowIfSealed();
        _codec.WriteClear(GetWriter());
        ApplyClear();
        SignalReaders(DurableWatchMode.Staged);
    }

    public void Seal()
    {
        if (_isSealed)
        {
            return;
        }

        _codec.WriteSeal(GetWriter());
        _isSealed = true;
        if (_drafts is { } drafts)
        {
            while (drafts.Open.Count > 0)
            {
                CloseDraft(drafts.Open[^1], DraftOutcome.Abandoned(LogDraftAbandonReason.Sealed, message: null));
            }
        }
    }

    public IDurableLogDraft<T> BeginDraft()
    {
        ThrowIfSealed();
        var drafts = _drafts ??= new DraftTable();
        var draft = new Draft(this, new LogDraftId(drafts.ActivationId, ++drafts.LastNumber));
        drafts.Open.Add(draft);
        return draft;
    }

    public IAsyncEnumerable<LogEvent<T>> ReadAsync(
        long fromSequence = 0,
        DurableWatchMode mode = DurableWatchMode.Committed,
        LogDraftId? resumeDraftId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromSequence);
        DurableStateObservation.ValidateWatch(mode, capacity: 1);
        return ReadCoreAsync(DurableStateObservation.GetRequiredManager(_manager), fromSequence, mode, resumeDraftId, cancellationToken);
    }

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        _items.Clear();
        _firstSequence = 0;
        _resetSequence = 0;
        _capturedSequence = 0;
        _committedSequence = 0;
        _capturedFirstSequence = 0;
        _committedFirstSequence = 0;
        _isSealed = false;
        _isSealCaptured = false;
        _isSealCommitted = false;
        _resetCount++;
        _writer = writer;
        if (_drafts is { } drafts)
        {
            // Recorded commits describe entries which no longer exist.
            drafts.ClearOutcomes();
            while (drafts.Open.Count > 0)
            {
                CloseDraft(drafts.Open[^1], DraftOutcome.Abandoned(LogDraftAbandonReason.Reset, message: null));
            }
        }

        SignalReaders(DurableWatchMode.Committed);
    }

    void IStateMachine.OnRecoveryCompleted()
    {
        _capturedSequence = _committedSequence = NextSequence;
        _capturedFirstSequence = _committedFirstSequence = _firstSequence;
        _isSealCaptured = _isSealCommitted = _isSealed;
        SignalReaders(DurableWatchMode.Committed);
    }

    // Entries and commands are journaled when they are made. Capturing records which of them this write makes durable.
    void IStateMachine.WritePendingEntries(JournalStreamWriter writer) => CaptureWrite();

    void IStateMachine.WriteSnapshot(JournalStreamWriter writer)
    {
        _codec.WriteSnapshot(_firstSequence, _resetSequence, _isSealed, _items, writer);
        CaptureWrite();
    }

    // The manager publishes the write's watermark after this callback, which wakes readers.
    void IStateMachine.OnWriteCompleted()
    {
        _committedSequence = Math.Max(_committedSequence, _capturedSequence);
        _committedFirstSequence = Math.Max(_committedFirstSequence, _capturedFirstSequence);
        _isSealCommitted |= _isSealCaptured;
    }

    void IDurableLogCommandHandler<T>.ApplyAppend(T item) => _items.Add(item);

    void IDurableLogCommandHandler<T>.ApplyTrimBefore(long sequence)
    {
        if (sequence < 0 || sequence > NextSequence)
        {
            throw new InvalidOperationException(
                $"Malformed durable log entry: cannot trim before sequence {sequence} when the next sequence is {NextSequence}.");
        }

        ApplyTrimBefore(sequence);
    }

    void IDurableLogCommandHandler<T>.ApplyClear() => ApplyClear();

    void IDurableLogCommandHandler<T>.ApplySeal() => _isSealed = true;

    void IDurableLogCommandHandler<T>.Reset(long firstSequence, long resetSequence, bool isSealed, int capacityHint)
    {
        if (resetSequence < 0 || firstSequence < resetSequence)
        {
            throw new InvalidOperationException(
                $"Malformed durable log snapshot: the first sequence {firstSequence} precedes the reset sequence {resetSequence}, or the reset sequence is negative.");
        }

        _items.Clear();
        _items.EnsureCapacity(capacityHint);
        _firstSequence = firstSequence;
        _resetSequence = resetSequence;
        _isSealed = isSealed;
    }

    private void CaptureWrite()
    {
        _capturedSequence = NextSequence;
        _capturedFirstSequence = _firstSequence;
        _isSealCaptured = _isSealed;
    }

    // Appends the items as one journal entry, so that one write makes all of them durable.
    private long AppendRange(ReadOnlySpan<T> items)
    {
        ThrowIfSealed();
        var sequence = NextSequence;
        if (!items.IsEmpty)
        {
            _codec.WriteAppend(items, GetWriter());
            _items.AddRange(items);
            SignalReaders(DurableWatchMode.Staged);
        }

        return sequence;
    }

    // The clear consumes one sequence number, so a reader's position shows whether it observed the replaced history.
    private void ApplyClear()
    {
        var sequence = NextSequence + 1;
        _items.Clear();
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
    }

    private bool TryGetIndex(long sequence, out int index)
    {
        if (sequence >= _firstSequence && sequence < NextSequence)
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
        JournaledStateMutationFence.ThrowIfClosed();
        return _writer;
    }

    private DurableChangeStatus GetEntryStatus(long sequence) =>
        sequence < _committedSequence ? DurableChangeStatus.Committed : DurableChangeStatus.Staged;

    private void SignalReaders(DurableWatchMode visibility)
    {
        if (DurableStateObservation.TryGetTopic(_manager, this, out var topic))
        {
            topic.Signal(visibility);
        }
    }

    private void CloseDraft(Draft draft, DraftOutcome outcome)
    {
        Debug.Assert(draft.IsOpen);
        var drafts = _drafts!;
        var removed = drafts.Open.Remove(draft);
        Debug.Assert(removed);
        draft.Close(outcome);
        drafts.RecordOutcome(draft.Id.Number, outcome);
        SignalReaders(DurableWatchMode.Drafts);
    }

    // Follows the draft a reconnecting reader was viewing: its items while it is open, otherwise its recorded outcome.
    private FollowedDraft ResumeDraft(LogDraftId id)
    {
        if (_drafts is not { } drafts || drafts.ActivationId != id.ActivationId)
        {
            return new(id, DraftOutcome.Abandoned(LogDraftAbandonReason.PreviousActivation, message: null));
        }

        foreach (var draft in drafts.Open)
        {
            if (draft.Id == id)
            {
                return new(draft);
            }
        }

        return new(id, drafts.TryGetOutcome(id.Number, out var outcome)
            ? outcome
            : DraftOutcome.Abandoned(LogDraftAbandonReason.Unknown, message: null));
    }

    private async IAsyncEnumerable<LogEvent<T>> ReadCoreAsync(
        JournaledStateManager manager,
        long fromSequence,
        DurableWatchMode mode,
        LogDraftId? resumeDraftId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hub = manager.GetOrCreateObservers();
        var reader = new Reader(hub, this, mode, fromSequence, _resetCount, catchUpSequence: _committedSequence);
        reader.KnownFirstSequence = Math.Min(fromSequence, GetVisibleFirstSequence(reader));
        if (reader.IncludesDrafts && resumeDraftId is { } draftId)
        {
            reader.FollowedDrafts = [ResumeDraft(draftId)];
        }

        hub.Add(reader);
        using var registration = DurableStateObservation.RegisterCancellation(reader, cancellationToken);
        try
        {
            while (true)
            {
                while (TryGetNextEvent(reader, out var logEvent))
                {
                    yield return logEvent;
                    if (logEvent.Kind is LogEventKind.Sealed)
                    {
                        yield break;
                    }
                }

                if (reader.CheckCompleted())
                {
                    yield break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                await reader.WaitAsync();
            }
        }
        finally
        {
            hub.Remove(reader);
        }
    }

    // Readers without staged changes observe a trim once it is durable.
    private long GetVisibleFirstSequence(Reader reader) => reader.IncludesStaged ? _firstSequence : _committedFirstSequence;

    // Computes the reader's next event from the current log state. Called between yields, so each call observes
    // mutations made while the reader was suspended.
    private bool TryGetNextEvent(Reader reader, out LogEvent<T> logEvent)
    {
        if (reader.FollowedDrafts is { Count: > 0 } followed && TryGetDraftOutcomeEvent(reader, followed, out logEvent))
        {
            return true;
        }

        if (reader.ResetCount != _resetCount)
        {
            // The journal was deleted, which restarted the sequence. Commits recorded before then describe deleted entries.
            reader.ResetCount = _resetCount;
            reader.FollowedDrafts?.RemoveAll(static followedDraft => followedDraft.TryGetOutcome(out _));

            // The entries which were committed when the read began no longer exist.
            reader.CatchUpSequence = 0;
            logEvent = ResetReader(reader, GetVisibleFirstSequence(reader), DurableChangeStatus.Committed, unconfirmedSequence: -1);
            return true;
        }

        if (reader.Cursor > NextSequence)
        {
            logEvent = ResetReader(reader, GetVisibleFirstSequence(reader), DurableChangeStatus.Committed, unconfirmedSequence: -1);
            return true;
        }

        if (reader.Cursor < _resetSequence)
        {
            // The clear consumed sequence _resetSequence - 1, so a write which captured that sequence made the clear durable.
            var isClearCommitted = _resetSequence <= _committedSequence;
            if (reader.IncludesStaged)
            {
                logEvent = isClearCommitted
                    ? ResetReader(reader, _firstSequence, DurableChangeStatus.Committed, unconfirmedSequence: -1)
                    : ResetReader(reader, _firstSequence, DurableChangeStatus.Staged, unconfirmedSequence: _resetSequence - 1);
                return true;
            }

            if (isClearCommitted)
            {
                logEvent = ResetReader(reader, _committedFirstSequence, DurableChangeStatus.Committed, unconfirmedSequence: -1);
                return true;
            }

            // Otherwise, the reader awaits the clear's write. The clear discarded every committed entry.
        }

        var firstSequence = GetVisibleFirstSequence(reader);
        if (firstSequence > reader.KnownFirstSequence)
        {
            reader.KnownFirstSequence = firstSequence;
            reader.Cursor = Math.Max(reader.Cursor, firstSequence);
            logEvent = new()
            {
                Kind = LogEventKind.Trimmed,
                Sequence = firstSequence,
                Status = firstSequence <= _committedFirstSequence ? DurableChangeStatus.Committed : DurableChangeStatus.Staged,
            };
            return true;
        }

        if (!reader.IsCaughtUp && reader.Cursor >= reader.CatchUpSequence)
        {
            reader.IsCaughtUp = true;
            logEvent = new() { Kind = LogEventKind.CaughtUp, Sequence = reader.Cursor, Status = DurableChangeStatus.Committed };
            return true;
        }

        // A committed reader positioned before an uncommitted trim awaits the trim's write.
        var limit = reader.IncludesStaged ? NextSequence : _committedSequence;
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

        if (reader.UnconfirmedSequence >= 0 && reader.UnconfirmedSequence < _committedSequence)
        {
            var watermark = Math.Min(_committedSequence, reader.Cursor);
            reader.UnconfirmedSequence = watermark < reader.Cursor ? watermark : -1;
            logEvent = new() { Kind = LogEventKind.Watermark, Sequence = watermark, Status = DurableChangeStatus.Committed };
            return true;
        }

        if (_isSealCommitted && reader.Cursor >= NextSequence && reader.UnconfirmedSequence < 0)
        {
            Debug.Assert(reader.IsCaughtUp && _drafts is not { Open.Count: > 0 });
            logEvent = new() { Kind = LogEventKind.Sealed, Sequence = NextSequence, Status = DurableChangeStatus.Committed };
            return true;
        }

        if (reader.IncludesDrafts && _drafts is { Open.Count: > 0 } drafts && TryGetDraftItemEvent(reader, drafts, out logEvent))
        {
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

            if (outcome.IsCommitted)
            {
                // Readers without staged entries learn of a commit once its entries are durable.
                var end = outcome.Sequence + outcome.Count;
                if (end > _committedSequence && !reader.IncludesStaged)
                {
                    continue;
                }

                followed.RemoveAt(i);
                logEvent = new()
                {
                    Kind = LogEventKind.DraftCommitted,
                    Sequence = outcome.Sequence,
                    Count = outcome.Count,
                    DraftId = followedDraft.Id,
                    Status = end <= _committedSequence ? DurableChangeStatus.Committed : DurableChangeStatus.Staged,
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
    /// How a draft closed: the range of entries which replaced it, or why it was abandoned.
    /// </summary>
    private readonly record struct DraftOutcome(long Sequence, int Count, LogDraftAbandonReason AbandonReason, string? Message)
    {
        public bool IsCommitted => AbandonReason is LogDraftAbandonReason.None;

        public static DraftOutcome Committed(long sequence, int count) => new(sequence, count, LogDraftAbandonReason.None, Message: null);

        public static DraftOutcome Abandoned(LogDraftAbandonReason reason, string? message) => new(0, 0, reason, message);
    }

    /// <summary>
    /// The open drafts of one log instance and the outcomes of its most recently closed drafts.
    /// </summary>
    private sealed class DraftTable
    {
        // Reconnecting readers learn the outcomes of this many recently closed drafts.
        private const int MaxRecentOutcomes = 16;

        private (long Number, DraftOutcome Outcome)[] _recentOutcomes = [];
        private int _recentCount;
        private int _oldestRecent;

        public Guid ActivationId { get; } = Guid.NewGuid();

        public long LastNumber { get; set; }

        public List<Draft> Open { get; } = [];

        public void RecordOutcome(long number, DraftOutcome outcome)
        {
            if (_recentCount < MaxRecentOutcomes)
            {
                if (_recentCount == _recentOutcomes.Length)
                {
                    Array.Resize(ref _recentOutcomes, Math.Max(4, _recentCount * 2));
                }

                _recentOutcomes[_recentCount++] = (number, outcome);
                return;
            }

            _recentOutcomes[_oldestRecent] = (number, outcome);
            _oldestRecent = (_oldestRecent + 1) % MaxRecentOutcomes;
        }

        public bool TryGetOutcome(long number, out DraftOutcome outcome)
        {
            for (var i = 0; i < _recentCount; i++)
            {
                if (_recentOutcomes[i].Number == number)
                {
                    outcome = _recentOutcomes[i].Outcome;
                    return true;
                }
            }

            outcome = default;
            return false;
        }

        public void ClearOutcomes()
        {
            Array.Clear(_recentOutcomes);
            _recentCount = 0;
            _oldestRecent = 0;
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
            ThrowIfClosed();
            var sequence = log.AppendRange(entries);
            log.CloseDraft(this, DraftOutcome.Committed(sequence, entries.Length));
            return sequence;
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
            LogDraftAbandonReason.Reset => "the log's journal was deleted",
            LogDraftAbandonReason.Sealed => "the log was sealed",
            _ => reason.ToString(),
        };
    }

    private sealed class Reader(JournaledStateObservers hub, DurableLog<T> log, DurableWatchMode mode, long cursor, int resetCount, long catchUpSequence)
        : StateWatcher(hub, log, mode)
    {
        public bool IncludesDrafts => (Mode & DurableWatchMode.Drafts) != 0;

        public long Cursor { get; set; } = cursor;

        // The first retained sequence the reader has been told of. A higher first sequence is reported as a trim.
        public long KnownFirstSequence { get; set; }

        // The first sequence delivered as staged which no watermark has confirmed, or -1.
        public long UnconfirmedSequence { get; set; } = -1;

        // The log's reset count when the reader last observed a deletion.
        public int ResetCount { get; set; } = resetCount;

        // The position after the entries which were committed when the read began.
        public long CatchUpSequence { get; set; } = catchUpSequence;

        public bool IsCaughtUp { get; set; }

        public List<FollowedDraft>? FollowedDrafts { get; set; }
    }

    /// <summary>
    /// A draft which a reader follows: an open draft, or the recorded outcome of a draft which closed before the reader resumed it.
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
