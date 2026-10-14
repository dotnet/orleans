using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Orleans.Journaling;

/// <summary>
/// Represents an append-only, observable log whose entries are recorded in a journal.
/// </summary>
/// <typeparam name="T">The type of entries in the log.</typeparam>
/// <remarks>
/// <para>
/// Each appended entry receives the next value of a durable, monotonically increasing sequence number.
/// Sequence numbers are never reused: <see cref="TrimBefore"/> discards old entries while preserving the sequence.
/// Enumeration yields the retained entries in sequence order.
/// </para>
/// <para>
/// A draft streams volatile content to readers which include drafts before its durable entries exist. Use drafts for
/// incremental output, such as model responses, whose final form is committed as one or more entries.
/// </para>
/// </remarks>
public interface IDurableLog<T> : IReadOnlyCollection<T>
{
    /// <summary>
    /// Gets the sequence number of the first retained entry.
    /// </summary>
    long FirstSequence { get; }

    /// <summary>
    /// Gets the sequence number which the next appended entry receives.
    /// </summary>
    long NextSequence { get; }

    /// <summary>
    /// Gets the sequence number after the last committed entry. Entries with lower sequence numbers are durable.
    /// </summary>
    long CommittedSequence { get; }

    /// <summary>
    /// Gets the retained entry with the specified sequence number.
    /// </summary>
    /// <param name="sequence">The entry's sequence number.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The log does not retain an entry with <paramref name="sequence"/>.</exception>
    T this[long sequence] { get; }

    /// <summary>
    /// Attempts to get the retained entry with the specified sequence number.
    /// </summary>
    /// <param name="sequence">The entry's sequence number.</param>
    /// <param name="item">The entry, if retained.</param>
    /// <returns><see langword="true"/> if the log retains an entry with <paramref name="sequence"/>; otherwise, <see langword="false"/>.</returns>
    bool TryGet(long sequence, [MaybeNullWhen(false)] out T item);

    /// <summary>
    /// Appends an entry to the log.
    /// </summary>
    /// <param name="item">The entry.</param>
    /// <returns>The entry's sequence number.</returns>
    long Append(T item);

    /// <summary>
    /// Discards entries with sequence numbers lower than <paramref name="sequence"/>.
    /// </summary>
    /// <param name="sequence">The sequence number of the first entry to retain.</param>
    /// <remarks>
    /// Trimming bounds retention and preserves <see cref="NextSequence"/>. Readers positioned before the trim point
    /// receive <see cref="LogEventKind.Reset"/> and continue from <see cref="FirstSequence"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sequence"/> is negative or greater than <see cref="NextSequence"/>.</exception>
    void TrimBefore(long sequence);

    /// <summary>
    /// Opens a draft which streams volatile items to readers which include drafts.
    /// </summary>
    /// <returns>The open draft. Dispose it to abandon the draft unless it was closed.</returns>
    IDurableLogDraft<T> BeginDraft();

    /// <summary>
    /// Reads the log from a sequence number, then waits for new entries.
    /// </summary>
    /// <param name="fromSequence">The sequence number of the first entry to read.</param>
    /// <param name="mode">
    /// The entries and events to deliver. <see cref="DurableWatchMode.Staged"/> adds staged entries and watermarks, and
    /// <see cref="DurableWatchMode.Drafts"/> adds draft events.
    /// </param>
    /// <param name="resumeDraftId">
    /// The draft a reconnecting reader with <see cref="DurableWatchMode.Drafts"/> was viewing. When the draft is still open,
    /// the stream repeats its items from the beginning. When it closed in this activation, the stream reports its outcome:
    /// <see cref="LogEventKind.DraftCommitted"/> with the entries which replaced it, or <see cref="LogEventKind.DraftAbandoned"/>
    /// with its <see cref="LogDraftAbandonReason"/>. The log retains a bounded number of recent outcomes, and reports
    /// <see cref="LogDraftAbandonReason.Unknown"/> for an older draft and <see cref="LogDraftAbandonReason.PreviousActivation"/>
    /// for a draft opened by another activation.
    /// </param>
    /// <param name="cancellationToken">The token which ends the read.</param>
    /// <returns>
    /// A stream of entries in sequence order. With <see cref="DurableWatchMode.Staged"/>, the stream also delivers staged
    /// entries and watermarks. With <see cref="DurableWatchMode.Drafts"/>, it also delivers draft events. Without
    /// <see cref="DurableWatchMode.Staged"/>, the stream reports a committed draft once the entry which replaced it is durable.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Readers re-read retained entries from their position rather than buffering them, so a slow reader retains no entries.
    /// When <paramref name="fromSequence"/> precedes <see cref="FirstSequence"/> or follows <see cref="NextSequence"/>,
    /// the stream begins with <see cref="LogEventKind.Reset"/> and continues from <see cref="FirstSequence"/>.
    /// </para>
    /// <para>
    /// To resume after a disconnection, pass the sequence after the last committed entry the reader observed, which is the
    /// latest <see cref="LogEventKind.Watermark"/> sequence for speculative readers.
    /// Enumerate the stream in the owning activation's execution context.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fromSequence"/> is negative or <paramref name="mode"/> is undefined.</exception>
    IAsyncEnumerable<LogEvent<T>> ReadAsync(
        long fromSequence = 0,
        DurableWatchMode mode = DurableWatchMode.Committed,
        LogDraftId? resumeDraftId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Streams volatile items to the readers of an <see cref="IDurableLog{T}"/> which include drafts, until the draft commits
/// durable entries or is abandoned.
/// </summary>
/// <typeparam name="T">The type of items and entries.</typeparam>
/// <remarks>
/// <para>
/// The draft holds its items in memory only while it is open. Closing the draft releases them, so a reader which has not
/// read every item when the draft closes receives the draft's outcome next and never receives the remaining items. The
/// committed entries carry the draft's final content.
/// </para>
/// <para>
/// The draft closes when its owner commits, abandons, or disposes it, and when the log closes it because the log's journal
/// was deleted. Disposing an open draft abandons it. Use the draft in the owning activation's execution context and check
/// <see cref="IsOpen"/> before committing a draft which the log may have closed.
/// </para>
/// </remarks>
public interface IDurableLogDraft<T> : IDisposable
{
    /// <summary>
    /// Gets the draft's identifier.
    /// </summary>
    LogDraftId Id { get; }

    /// <summary>
    /// Gets a value indicating whether the draft is open.
    /// </summary>
    /// <remarks>
    /// The value becomes <see langword="false"/> when the draft is committed, abandoned, or disposed, and when the log closes it.
    /// <see cref="Write"/> and <see cref="Commit"/> throw <see cref="InvalidOperationException"/> for a closed draft.
    /// </remarks>
    bool IsOpen { get; }

    /// <summary>
    /// Gets the number of items written to the open draft.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Writes a volatile item and delivers it to readers which include drafts.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <exception cref="InvalidOperationException">The draft is closed. The message states why.</exception>
    void Write(T item);

    /// <summary>
    /// Closes the draft and appends the durable entries which replace its items.
    /// </summary>
    /// <param name="entries">The entries, in order. Pass none to close the draft cleanly without appending entries.</param>
    /// <returns>
    /// The sequence number of the first entry. When <paramref name="entries"/> is empty, the sequence number which the next
    /// appended entry receives.
    /// </returns>
    /// <remarks>
    /// The entries are appended as one journal entry, so one journal write makes all of them durable. Readers receive
    /// <see cref="LogEventKind.DraftCommitted"/> with the committed range, then the entries. If an entry cannot be encoded,
    /// the exception propagates, no entry is appended, and the draft remains open.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The draft is closed. The message states why.</exception>
    long Commit(params ReadOnlySpan<T> entries);

    /// <summary>
    /// Closes the draft without entries and reports <see cref="LogEventKind.DraftAbandoned"/> with
    /// <see cref="LogDraftAbandonReason.Abandoned"/>.
    /// </summary>
    /// <param name="message">A description of the failure reported to readers in <see cref="LogEvent{T}.Message"/>.</param>
    /// <remarks>Abandoning a closed draft has no effect. Use <see cref="Commit"/> with no entries to close a draft without a failure.</remarks>
    void Abandon(string? message = null);
}

/// <summary>
/// Identifies a draft of an <see cref="IDurableLog{T}"/>.
/// </summary>
/// <remarks>
/// <see cref="ActivationId"/> identifies the log instance which opened the draft. A reader which reconnects to a new
/// activation can therefore learn that its draft was abandoned.
/// </remarks>
[GenerateSerializer, Immutable]
public readonly record struct LogDraftId
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LogDraftId"/> struct.
    /// </summary>
    /// <param name="activationId">The identifier of the log instance which opened the draft.</param>
    /// <param name="number">The draft's number within that instance.</param>
    public LogDraftId(Guid activationId, long number)
    {
        ActivationId = activationId;
        Number = number;
    }

    /// <summary>
    /// Gets the identifier of the log instance, and therefore the activation, which opened the draft.
    /// </summary>
    [Id(0)]
    public Guid ActivationId { get; }

    /// <summary>
    /// Gets the draft's number within its log instance.
    /// </summary>
    [Id(1)]
    public long Number { get; }

    /// <summary>
    /// Formats the identifier as <c>{ActivationId:N}.{Number}</c>.
    /// </summary>
    /// <returns>The formatted identifier.</returns>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{ActivationId:N}.{Number}");

    /// <summary>
    /// Parses an identifier formatted by <see cref="ToString"/>.
    /// </summary>
    /// <param name="value">The formatted identifier.</param>
    /// <param name="result">The parsed identifier.</param>
    /// <returns><see langword="true"/> if <paramref name="value"/> is a valid identifier; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(ReadOnlySpan<char> value, out LogDraftId result)
    {
        var separator = value.IndexOf('.');
        if (separator > 0
            && Guid.TryParseExact(value[..separator], "N", out var activationId)
            && long.TryParse(value[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            result = new(activationId, number);
            return true;
        }

        result = default;
        return false;
    }
}

/// <summary>
/// Identifies the kind of a <see cref="LogEvent{T}"/>.
/// </summary>
[GenerateSerializer]
public enum LogEventKind : byte
{
    /// <summary>
    /// <see cref="LogEvent{T}.Item"/> is the entry with sequence number <see cref="LogEvent{T}.Sequence"/>.
    /// </summary>
    Entry = 0,

    /// <summary>
    /// The reader's position is outside the retained log, because entries were trimmed or the log was reset.
    /// The stream continues from <see cref="LogEvent{T}.Sequence"/>, the first retained entry.
    /// </summary>
    Reset = 1,

    /// <summary>
    /// Entries with sequence numbers lower than <see cref="LogEvent{T}.Sequence"/> are committed.
    /// </summary>
    Watermark = 2,

    /// <summary>
    /// <see cref="LogEvent{T}.Item"/> is the volatile item at position <see cref="LogEvent{T}.Sequence"/> of draft
    /// <see cref="LogEvent{T}.DraftId"/>. Position zero begins the draft's content.
    /// </summary>
    DraftItem = 3,

    /// <summary>
    /// Draft <see cref="LogEvent{T}.DraftId"/> closed, and its content is replaced by the <see cref="LogEvent{T}.Count"/>
    /// entries which begin at sequence number <see cref="LogEvent{T}.Sequence"/>. Discard its items. A count of zero means
    /// that the draft closed cleanly without entries.
    /// </summary>
    DraftCommitted = 4,

    /// <summary>
    /// Draft <see cref="LogEvent{T}.DraftId"/> closed without entries for the reason in <see cref="LogEvent{T}.AbandonReason"/>.
    /// Discard its items.
    /// </summary>
    DraftAbandoned = 5,
}

/// <summary>
/// Identifies why a draft of an <see cref="IDurableLog{T}"/> closed without entries.
/// </summary>
[GenerateSerializer]
public enum LogDraftAbandonReason : byte
{
    /// <summary>
    /// The event does not describe an abandoned draft.
    /// </summary>
    None = 0,

    /// <summary>
    /// The owner abandoned the draft with <see cref="IDurableLogDraft{T}.Abandon"/>.
    /// </summary>
    Abandoned = 1,

    /// <summary>
    /// The owner disposed the draft without closing it.
    /// </summary>
    Disposed = 2,

    /// <summary>
    /// The log's journal was deleted.
    /// </summary>
    Reset = 3,

    /// <summary>
    /// Another activation opened the draft. Drafts exist only in memory, so its content is lost.
    /// </summary>
    PreviousActivation = 4,

    /// <summary>
    /// This activation does not know the draft's outcome, because the draft closed too long ago or never existed.
    /// </summary>
    Unknown = 5,
}

/// <summary>
/// Describes an entry or event read from an <see cref="IDurableLog{T}"/>.
/// </summary>
/// <typeparam name="T">The type of entries in the log.</typeparam>
[GenerateSerializer]
public readonly record struct LogEvent<T>
{
    /// <summary>
    /// Gets the kind of event.
    /// </summary>
    [Id(0)]
    public LogEventKind Kind { get; init; }

    /// <summary>
    /// Gets the entry sequence number, the reset or watermark position, the item's position within its draft, or the
    /// first sequence number of a committed draft's entries.
    /// </summary>
    [Id(1)]
    public long Sequence { get; init; }

    /// <summary>
    /// Gets the entry for <see cref="LogEventKind.Entry"/> or the draft item for <see cref="LogEventKind.DraftItem"/>.
    /// </summary>
    [Id(2)]
    public T? Item { get; init; }

    /// <summary>
    /// Gets the draft identifier for draft events.
    /// </summary>
    [Id(3)]
    public LogDraftId? DraftId { get; init; }

    /// <summary>
    /// Gets the description which the owner passed to <see cref="IDurableLogDraft{T}.Abandon"/>, for <see cref="LogEventKind.DraftAbandoned"/>.
    /// </summary>
    [Id(4)]
    public string? Message { get; init; }

    /// <summary>
    /// Gets the durability status of the event.
    /// </summary>
    [Id(5)]
    public DurableChangeStatus Status { get; init; }

    /// <summary>
    /// Gets the number of entries which replaced a draft, for <see cref="LogEventKind.DraftCommitted"/>.
    /// </summary>
    [Id(6)]
    public int Count { get; init; }

    /// <summary>
    /// Gets why a draft closed without entries, for <see cref="LogEventKind.DraftAbandoned"/>.
    /// </summary>
    [Id(7)]
    public LogDraftAbandonReason AbandonReason { get; init; }
}
