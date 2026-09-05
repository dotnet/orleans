using System;

namespace Orleans.Streams;

/// <summary>
/// Describes the outcome of acquiring a queue cache cursor.
/// </summary>
public enum QueueCacheCursorResultKind
{
    /// <summary>
    /// The result is not initialized.
    /// </summary>
    Invalid,

    /// <summary>
    /// A cursor was acquired.
    /// </summary>
    Success,

    /// <summary>
    /// The requested position is older than the messages retained by the cache.
    /// </summary>
    CacheMiss,

    /// <summary>
    /// The requested operation is not supported by the cache.
    /// </summary>
    NotSupported,
}

/// <summary>
/// Describes the outcome of advancing a queue cache cursor.
/// </summary>
public enum QueueCacheCursorMoveResultKind
{
    /// <summary>
    /// The result is not initialized.
    /// </summary>
    Invalid,

    /// <summary>
    /// The cursor advanced to a message.
    /// </summary>
    Success,

    /// <summary>
    /// No message is currently available.
    /// </summary>
    NoData,

    /// <summary>
    /// The cursor position is older than the messages retained by the cache.
    /// </summary>
    CacheMiss,
}

/// <summary>
/// Describes a queue cache miss.
/// </summary>
public readonly struct QueueCacheMissInfo
{
    private readonly object? _requested;
    private readonly object? _low;
    private readonly object? _high;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueCacheMissInfo"/> struct.
    /// </summary>
    /// <param name="requested">The requested sequence token.</param>
    /// <param name="low">The earliest available sequence token.</param>
    /// <param name="high">The latest available sequence token.</param>
    public QueueCacheMissInfo(StreamSequenceToken requested, StreamSequenceToken low, StreamSequenceToken high)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(low);
        ArgumentNullException.ThrowIfNull(high);
        _requested = requested;
        _low = low;
        _high = high;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueCacheMissInfo"/> struct.
    /// </summary>
    /// <param name="requested">The requested sequence token.</param>
    /// <param name="low">The earliest available sequence token.</param>
    /// <param name="high">The latest available sequence token.</param>
    public QueueCacheMissInfo(string? requested, string? low, string? high)
    {
        _requested = requested;
        _low = low;
        _high = high;
    }

    /// <summary>
    /// Gets the requested sequence token.
    /// </summary>
    public string? Requested => _requested?.ToString();

    /// <summary>
    /// Gets the earliest available sequence token.
    /// </summary>
    public string? Low => _low?.ToString();

    /// <summary>
    /// Gets the latest available sequence token.
    /// </summary>
    public string? High => _high?.ToString();

    /// <summary>
    /// Gets the requested sequence token when it is available.
    /// </summary>
    public StreamSequenceToken? RequestedToken => _requested as StreamSequenceToken;

    /// <summary>
    /// Gets the earliest available sequence token when it is available.
    /// </summary>
    public StreamSequenceToken? LowToken => _low as StreamSequenceToken;

    /// <summary>
    /// Gets the latest available sequence token when it is available.
    /// </summary>
    public StreamSequenceToken? HighToken => _high as StreamSequenceToken;

    /// <summary>
    /// Creates an exception representing this cache miss.
    /// </summary>
    /// <returns>A queue cache miss exception.</returns>
    public QueueCacheMissException ToException() => new(Requested, Low, High);
}

/// <summary>
/// Represents the result of acquiring a queue cache cursor.
/// </summary>
/// <typeparam name="TCursor">The cursor type.</typeparam>
/// <remarks>
/// A <see cref="QueueCacheCursorResultKind.Success"/> result contains a non-null <see cref="Cursor"/>.
/// All other results contain no cursor. A <see cref="QueueCacheCursorResultKind.CacheMiss"/> result
/// contains <see cref="CacheMiss"/> details, while other results contain no cache-miss details.
/// </remarks>
public readonly struct QueueCacheCursorResult<TCursor> where TCursor : class
{
    private readonly TCursor? _cursor;
    private readonly QueueCacheMissInfo _cacheMiss;

    private QueueCacheCursorResult(
        QueueCacheCursorResultKind kind,
        TCursor? cursor = null,
        QueueCacheMissInfo cacheMiss = default)
    {
        Kind = kind;
        _cursor = cursor;
        _cacheMiss = cacheMiss;
    }

    /// <summary>
    /// Gets the result kind.
    /// </summary>
    public QueueCacheCursorResultKind Kind { get; }

    /// <summary>
    /// Gets the acquired cursor when <see cref="Kind"/> is <see cref="QueueCacheCursorResultKind.Success"/>.
    /// </summary>
    public TCursor? Cursor => Kind == QueueCacheCursorResultKind.Success ? _cursor : null;

    /// <summary>
    /// Gets the cache miss details when <see cref="Kind"/> is <see cref="QueueCacheCursorResultKind.CacheMiss"/>.
    /// </summary>
    public QueueCacheMissInfo? CacheMiss => Kind == QueueCacheCursorResultKind.CacheMiss ? _cacheMiss : null;

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    /// <param name="cursor">The acquired cursor.</param>
    /// <returns>A successful cursor result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cursor"/> is <see langword="null"/>.</exception>
    public static QueueCacheCursorResult<TCursor> FromCursor(TCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return new(QueueCacheCursorResultKind.Success, cursor);
    }

    /// <summary>
    /// Creates a cache miss result.
    /// </summary>
    /// <param name="cacheMiss">The cache miss details.</param>
    /// <returns>A cache miss result.</returns>
    public static QueueCacheCursorResult<TCursor> FromCacheMiss(QueueCacheMissInfo cacheMiss)
        => new(QueueCacheCursorResultKind.CacheMiss, cacheMiss: cacheMiss);

    /// <summary>
    /// Gets a result which indicates that the operation is not supported.
    /// </summary>
    public static QueueCacheCursorResult<TCursor> NotSupported { get; } = new(QueueCacheCursorResultKind.NotSupported);
}

/// <summary>
/// Represents the result of advancing a queue cache cursor.
/// </summary>
/// <remarks>
/// A <see cref="QueueCacheCursorMoveResultKind.Success"/> result indicates that the cursor has a valid
/// current item. All other results indicate that no current item was produced. A
/// <see cref="QueueCacheCursorMoveResultKind.CacheMiss"/> result contains <see cref="CacheMiss"/> details.
/// </remarks>
public readonly struct QueueCacheCursorMoveResult
{
    private readonly QueueCacheMissInfo _cacheMiss;

    private QueueCacheCursorMoveResult(
        QueueCacheCursorMoveResultKind kind,
        QueueCacheMissInfo cacheMiss = default)
    {
        Kind = kind;
        _cacheMiss = cacheMiss;
    }

    /// <summary>
    /// Gets the result kind.
    /// </summary>
    public QueueCacheCursorMoveResultKind Kind { get; }

    /// <summary>
    /// Gets the cache miss details when <see cref="Kind"/> is <see cref="QueueCacheCursorMoveResultKind.CacheMiss"/>.
    /// </summary>
    public QueueCacheMissInfo? CacheMiss => Kind == QueueCacheCursorMoveResultKind.CacheMiss ? _cacheMiss : null;

    /// <summary>
    /// Gets a successful cursor advancement result.
    /// </summary>
    public static QueueCacheCursorMoveResult Success { get; } = new(QueueCacheCursorMoveResultKind.Success);

    /// <summary>
    /// Gets a result which indicates that no message is currently available.
    /// </summary>
    public static QueueCacheCursorMoveResult NoData { get; } = new(QueueCacheCursorMoveResultKind.NoData);

    /// <summary>
    /// Creates a cache miss result.
    /// </summary>
    /// <param name="cacheMiss">The cache miss details.</param>
    /// <returns>A cache miss result.</returns>
    public static QueueCacheCursorMoveResult FromCacheMiss(QueueCacheMissInfo cacheMiss)
        => new(QueueCacheCursorMoveResultKind.CacheMiss, cacheMiss);
}
