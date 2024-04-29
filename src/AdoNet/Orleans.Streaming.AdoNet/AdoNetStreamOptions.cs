namespace Orleans.Configuration;

/// <summary>
/// Options for ADO.NET Streaming.
/// </summary>
public class AdoNetStreamOptions
{
    /// <summary>
    /// Gets or sets the ADO.NET invariant.
    /// </summary>
    public string Invariant { get; set; }

    /// <summary>
    /// Gets or sets the connection string.
    /// </summary>
    [Redact]
    public string ConnectionString { get; set; }

    /// <summary>
    /// The maximum number of attempts to deliver a message.
    /// The message is eventually moved to dead letters if these many attempts are made without success.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// The expiry timeout until a message is considered expired and moved to dead letters regardless of attempts.
    /// The message is only moved if the current attempt is also past its visibility timeout.
    /// </summary>
    public TimeSpan ExpiryTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The removal timeout until a failed message is deleted from the dead letters table.
    /// </summary>
    public TimeSpan RemovalTimeout { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Whether to delete messages from the dead letters table after <see cref="RemovalTimeout"/>.
    /// </summary>
    public bool RemoveDeadLetters { get; set; } = true;
}