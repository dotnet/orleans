using Orleans.Runtime;
using StackExchange.Redis;

namespace Orleans.Journaling;

/// <summary>
/// Options for configuring Redis journal storage.
/// </summary>
public sealed class RedisJournalStorageOptions
{
    /// <summary>
    /// The default compaction threshold, in bytes.
    /// </summary>
    public const long DEFAULT_COMPACTION_THRESHOLD_BYTES = 128L * 1024 * 1024;

    /// <summary>
    /// Gets or sets the Redis client configuration.
    /// </summary>
    [RedactRedisConfigurationOptions]
    public ConfigurationOptions? ConfigurationOptions { get; set; }

    /// <summary>
    /// Gets or sets the delegate used to create the Redis connection multiplexer.
    /// </summary>
    public Func<RedisJournalStorageOptions, Task<(IConnectionMultiplexer Multiplexer, bool IsShared)>> CreateMultiplexer { get; set; } = DefaultCreateMultiplexer;

    /// <summary>
    /// Gets or sets the stage of the silo lifecycle when storage should be initialized.
    /// </summary>
    public int InitStage { get; set; } = ServiceLifecycleStage.RuntimeInitialize;

    /// <summary>
    /// Gets or sets the Redis key prefix used by the provider.
    /// </summary>
    /// <remarks>
    /// If not set, the provider uses <c>{ServiceId}/journaling</c>.
    /// </remarks>
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// Gets or sets the delegate used to convert a journal id to the Redis key name component.
    /// </summary>
    public Func<JournalId, string> GetKeyName { get; set; } = static journalId => journalId.Value;

    /// <summary>
    /// Gets or sets the journal length, in bytes, at which <see cref="IJournalStorage.IsCompactionRequested"/> returns <see langword="true"/>.
    /// </summary>
    public long CompactionThresholdBytes { get; set; } = DEFAULT_COMPACTION_THRESHOLD_BYTES;

    /// <summary>
    /// Gets or sets the maximum number of bytes supplied to the journal consumer in each recovery segment.
    /// </summary>
    public int ReadChunkSize { get; set; } = 1024 * 1024;

    /// <summary>
    /// Creates the default Redis connection multiplexer.
    /// </summary>
    /// <param name="options">The Redis journal storage options.</param>
    /// <returns>The Redis connection multiplexer and a value indicating whether it is shared.</returns>
    public static async Task<(IConnectionMultiplexer Multiplexer, bool IsShared)> DefaultCreateMultiplexer(RedisJournalStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return (await ConnectionMultiplexer.ConnectAsync(options.ConfigurationOptions!).ConfigureAwait(false), false);
    }

    internal string GetKeyPrefix(string serviceId)
    {
        var keyPrefix = KeyPrefix ?? $"{serviceId}/journaling";
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        return keyPrefix;
    }

    internal string GetKeyNameForJournal(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        var keyName = GetKeyName(journalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        return keyName;
    }
}

internal sealed class RedactRedisConfigurationOptionsAttribute : RedactAttribute
{
    public override string Redact(object value) => value is ConfigurationOptions configuration
        ? configuration.ToString(includePassword: false)
        : base.Redact(value);
}
