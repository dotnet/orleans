namespace Orleans.Journaling.Cosmos;

/// <summary>
/// Options for configuring the Azure Cosmos DB log-based storage provider.
/// </summary>
public class CosmosLogStorageOptions : CosmosOptions
{
    /// <summary>
    /// The number of log entries after which compaction is requested.
    /// Given a threshold value of 10, compaction will occur on the 11th append.
    /// </summary>
    /// <remarks>
    /// <para>Valid range is [1-97]</para>
    /// </remarks>
    public int CompactionThreshold { get; set; } = DEFAULT_COMPACTION_THRESHOLD;

    /// <summary>
    /// The default value of <see cref="CompactionThreshold"/>.
    /// </summary>
    public const int DEFAULT_COMPACTION_THRESHOLD = 10;

    public CosmosLogStorageOptions()
    {
        ContainerName = "OrleansJournaling";
    }
}

internal class CosmosLogStorageOptionsValidator(CosmosLogStorageOptions options) :
    CosmosOptionsValidator<CosmosLogStorageOptions>(options, nameof(CosmosLogStorageOptions))
{
    private readonly CosmosLogStorageOptions _options = options;

    public override void ValidateConfiguration()
    {
        // Lower bound is 1 -> means compact after every entry.
        // Upper bound is 97 -> max of 98 (log deletes) + 1 (pending delete) + 1 (compacted creation) = 100 (exactly the cosmos TX batch limit)

        if (_options.CompactionThreshold < 1 || _options.CompactionThreshold > 97)
        {
            throw new OrleansConfigurationException($"{nameof(CosmosLogStorageOptions)}." +
                $"{nameof(CosmosLogStorageOptions.CompactionThreshold)} must be in range [1-97]");
        }

        base.ValidateConfiguration();
    }
}
