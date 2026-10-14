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
/// A draft streams volatile content to speculative readers before its durable entry exists. Use drafts for
/// incremental output, such as model responses, whose final form is committed as one entry.
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
    /// Opens a draft which streams volatile items to speculative readers.
    /// </summary>
    /// <returns>The open draft. Dispose it to abandon the draft unless it was committed.</returns>
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
    /// The draft a reconnecting reader with <see cref="DurableWatchMode.Drafts"/> was viewing. The stream reports <see cref="LogEventKind.DraftAbandoned"/>
    /// for it when it is no longer open, or repeats its items from the beginning when it is still open.
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
/// Streams volatile items to the speculative readers of an <see cref="IDurableLog{T}"/> until it commits one durable entry or is abandoned.
/// </summary>
/// <typeparam name="T">The type of items and entries.</typeparam>
/// <remarks>
/// The draft holds its items in memory only while it is open. Committing or abandoning the draft releases them.
/// Disposing an open draft abandons it.
/// </remarks>
public interface IDurableLogDraft<T> : IDisposable
{
    /// <summary>
    /// Gets the draft's identifier.
    /// </summary>
    LogDraftId Id { get; }

    /// <summary>
    /// Gets a value indicating whether the draft accepts items.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Gets the number of items written to the open draft.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Writes a volatile item and delivers it to speculative readers.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <exception cref="InvalidOperationException">The draft is closed.</exception>
    void Write(T item);

    /// <summary>
    /// Closes the draft and appends its durable entry to the log.
    /// </summary>
    /// <param name="entry">The durable entry which replaces the draft's items.</param>
    /// <returns>The entry's sequence number.</returns>
    /// <remarks>
    /// Speculative readers receive <see cref="LogEventKind.DraftCommitted"/> followed by the staged entry. The entry becomes
    /// durable when the next journal write is acknowledged.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The draft is closed.</exception>
    long Commit(T entry);

    /// <summary>
    /// Closes the draft without a durable entry and reports <see cref="LogEventKind.DraftAbandoned"/> to speculative readers.
    /// </summary>
    /// <param name="reason">The reason reported to readers.</param>
    /// <remarks>Abandoning a closed draft has no effect.</remarks>
    void Abandon(string? reason = null);
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
    /// Draft <see cref="LogEvent{T}.DraftId"/> closed and its content is replaced by the entry with sequence number <see cref="LogEvent{T}.Sequence"/>.
    /// </summary>
    DraftCommitted = 4,

    /// <summary>
    /// Draft <see cref="LogEvent{T}.DraftId"/> closed without a durable entry. Discard its items.
    /// </summary>
    DraftAbandoned = 5,
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
    /// Gets the entry sequence number, the reset or watermark position, or the item's position within its draft.
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
    /// Gets the reason for <see cref="LogEventKind.DraftAbandoned"/>.
    /// </summary>
    [Id(4)]
    public string? Reason { get; init; }

    /// <summary>
    /// Gets the durability status of the event.
    /// </summary>
    [Id(5)]
    public DurableChangeStatus Status { get; init; }
}
