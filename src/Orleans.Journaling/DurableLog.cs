using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

[DebuggerDisplay("Count = {Count}, NextSequence = {NextSequence}")]
internal sealed class DurableLog<T> : IDurableLog<T>, IStateMachine, IDurableLogCommandHandler<T>
{
    private const string AbandonedReason = "The draft was abandoned.";
    private const string DisposedReason = "The draft was disposed before it was committed.";
    private const string ResetReason = "The log was reset.";
    private const string ClosedReason = "The draft is no longer open.";
    private const string PreviousActivationReason = "The draft belonged to a previous activation.";

    private readonly IDurableLogCommandCodec<T> _codec;
    private readonly JournaledStateManager? _manager;
    private readonly List<T> _items = [];
    private JournalStreamWriter _writer;
    private long _firstSequence;

    // The next sequence when the most recent write was captured, and when the most recent acknowledged write was captured.
    private long _capturedSequence;
    private long _committedSequence;

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

    public long Append(T item)
    {
        var sequence = NextSequence;
        _codec.WriteAppend(item, GetWriter());
        _items.Add(item);
        SignalReaders(speculativeOnly: true);
        return sequence;
    }

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
        SignalReaders(speculativeOnly: false);
    }

    public IDurableLogDraft<T> BeginDraft()
    {
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
        _capturedSequence = 0;
        _committedSequence = 0;
        _writer = writer;
        if (_drafts is { } drafts)
        {
            while (drafts.Open.Count > 0)
            {
                CloseDraft(drafts.Open[^1], ResetReason);
            }
        }

        SignalReaders(speculativeOnly: false);
    }

    void IStateMachine.OnRecoveryCompleted()
    {
        _capturedSequence = NextSequence;
        _committedSequence = NextSequence;
        SignalReaders(speculativeOnly: false);
    }

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer)
    {
        // Entries are journaled when they are appended. Capturing records which entries this write makes durable.
        _capturedSequence = NextSequence;
    }

    void IStateMachine.WriteSnapshot(JournalStreamWriter writer)
    {
        _codec.WriteSnapshot(_firstSequence, _items, writer);
        _capturedSequence = NextSequence;
    }

    // The manager publishes the write's watermark after this callback, which wakes readers.
    void IStateMachine.OnWriteCompleted() => _committedSequence = Math.Max(_committedSequence, _capturedSequence);

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

    void IDurableLogCommandHandler<T>.Reset(long firstSequence, int capacityHint)
    {
        if (firstSequence < 0)
        {
            throw new InvalidOperationException($"Malformed durable log snapshot: the first sequence {firstSequence} is negative.");
        }

        _items.Clear();
        _items.EnsureCapacity(capacityHint);
        _firstSequence = firstSequence;
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
        return _writer;
    }

    private DurableChangeStatus GetEntryStatus(long sequence) =>
        sequence < _committedSequence ? DurableChangeStatus.Committed : DurableChangeStatus.Staged;

    private void SignalReaders(bool speculativeOnly)
    {
        if (DurableStateObservation.TryGetTopic(_manager, this, out var topic))
        {
            topic.Signal(speculativeOnly);
        }
    }

    private void CloseDraft(Draft draft, string? abandonReason)
    {
        Debug.Assert(draft.IsOpen);
        var removed = _drafts!.Open.Remove(draft);
        Debug.Assert(removed);
        draft.Close(abandonReason);
        SignalReaders(speculativeOnly: true);
    }

    private Draft? FindOpenDraft(LogDraftId id)
    {
        if (_drafts is { } drafts && drafts.ActivationId == id.ActivationId)
        {
            foreach (var draft in drafts.Open)
            {
                if (draft.Id == id)
                {
                    return draft;
                }
            }
        }

        return null;
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
        var reader = new Reader(new SignalWatcher(hub, this, mode), fromSequence);
        hub.Add(reader.Watcher);
        using var registration = DurableStateObservation.RegisterCancellation(reader.Watcher, cancellationToken);
        try
        {
            if (reader.IsSpeculative && resumeDraftId is { } draftId)
            {
                if (FindOpenDraft(draftId) is { } draft)
                {
                    reader.FollowedDrafts = [new FollowedDraft(draft)];
                }
                else
                {
                    yield return new()
                    {
                        Kind = LogEventKind.DraftAbandoned,
                        DraftId = draftId,
                        Reason = _drafts?.ActivationId == draftId.ActivationId ? ClosedReason : PreviousActivationReason,
                        Status = DurableChangeStatus.Draft,
                    };
                }
            }

            while (true)
            {
                while (TryGetNextEvent(reader, out var logEvent))
                {
                    yield return logEvent;
                }

                if (reader.Watcher.CheckCompleted())
                {
                    yield break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                await reader.Watcher.WaitAsync();
            }
        }
        finally
        {
            hub.Remove(reader.Watcher);
        }
    }

    // Computes the reader's next event from the current log state. Called between yields, so each call observes
    // mutations made while the reader was suspended.
    private bool TryGetNextEvent(Reader reader, out LogEvent<T> logEvent)
    {
        if (reader.FollowedDrafts is { } followed)
        {
            for (var i = 0; i < followed.Count; i++)
            {
                var draft = followed[i].Draft;
                if (draft.IsOpen)
                {
                    continue;
                }

                followed.RemoveAt(i);
                logEvent = draft.CommittedSequence >= 0
                    ? new()
                    {
                        Kind = LogEventKind.DraftCommitted,
                        Sequence = draft.CommittedSequence,
                        DraftId = draft.Id,
                        Status = GetEntryStatus(draft.CommittedSequence),
                    }
                    : new()
                    {
                        Kind = LogEventKind.DraftAbandoned,
                        DraftId = draft.Id,
                        Reason = draft.AbandonReason,
                        Status = DurableChangeStatus.Draft,
                    };
                return true;
            }
        }

        if (reader.Cursor < _firstSequence || reader.Cursor > NextSequence)
        {
            reader.Cursor = _firstSequence;
            reader.UnconfirmedSequence = -1;
            logEvent = new() { Kind = LogEventKind.Reset, Sequence = _firstSequence, Status = DurableChangeStatus.Committed };
            return true;
        }

        var limit = reader.IsSpeculative ? NextSequence : _committedSequence;
        if (reader.Cursor < limit)
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

        if (reader.IsSpeculative && _drafts is { Open.Count: > 0 } drafts)
        {
            var followedDrafts = reader.FollowedDrafts ??= [];
            foreach (var draft in drafts.Open)
            {
                if (!IsFollowed(followedDrafts, draft))
                {
                    followedDrafts.Add(new FollowedDraft(draft));
                }
            }

            foreach (var followedDraft in followedDrafts)
            {
                var draft = followedDraft.Draft;
                if (draft.IsOpen && followedDraft.Position < draft.Count)
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

    private sealed class DraftTable
    {
        public Guid ActivationId { get; } = Guid.NewGuid();

        public long LastNumber { get; set; }

        public List<Draft> Open { get; } = [];
    }

    private sealed class Draft(DurableLog<T> log, LogDraftId id) : IDurableLogDraft<T>
    {
        private List<T>? _items = [];

        public LogDraftId Id { get; } = id;

        public bool IsOpen => _items is not null;

        public int Count => _items?.Count ?? 0;

        public long CommittedSequence { get; private set; } = -1;

        public string? AbandonReason { get; private set; }

        public T GetItem(int position) => _items![position];

        public void Write(T item)
        {
            ThrowIfClosed();
            _items!.Add(item);
            log.SignalReaders(speculativeOnly: true);
        }

        public long Commit(T entry)
        {
            ThrowIfClosed();
            var sequence = log.Append(entry);
            CommittedSequence = sequence;
            log.CloseDraft(this, abandonReason: null);
            return sequence;
        }

        public void Abandon(string? reason = null)
        {
            if (IsOpen)
            {
                log.CloseDraft(this, reason ?? AbandonedReason);
            }
        }

        public void Dispose() => Abandon(DisposedReason);

        internal void Close(string? abandonReason)
        {
            AbandonReason = abandonReason;
            _items = null;
        }

        private void ThrowIfClosed()
        {
            if (!IsOpen)
            {
                throw new InvalidOperationException("The draft is closed.");
            }
        }
    }

    private sealed class Reader(SignalWatcher watcher, long cursor)
    {
        public SignalWatcher Watcher { get; } = watcher;

        public bool IsSpeculative => Watcher.Mode is DurableWatchMode.Speculative;

        public long Cursor { get; set; } = cursor;

        // The first sequence delivered as staged which no watermark has confirmed, or -1.
        public long UnconfirmedSequence { get; set; } = -1;

        public List<FollowedDraft>? FollowedDrafts { get; set; }
    }

    private sealed class FollowedDraft(Draft draft)
    {
        public Draft Draft { get; } = draft;

        public int Position { get; set; }
    }
}
