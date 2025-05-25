using Microsoft.CodeAnalysis.CodeActions;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Collections.Generic;

namespace Orleans.Journaling.Cosmos;

public class CosmosLogStorageOptions : CosmosOptions
{
    /// <summary>
    /// The number of log entries after which compaction is requested.
    /// </summary>
    /// <remarks>
    /// <para>Valid range is [1-97]</para>
    /// <para>Recomended value is 10</para>
    /// <para>1 means compact after every state update</para>
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
