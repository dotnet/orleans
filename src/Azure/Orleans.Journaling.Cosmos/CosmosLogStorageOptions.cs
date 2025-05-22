namespace Orleans.Journaling.Cosmos;

public class CosmosLogStorageOptions : CosmosOptions
{
    /// <summary>
    /// The number of log entries after which compaction is requested.
    /// </summary>
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
