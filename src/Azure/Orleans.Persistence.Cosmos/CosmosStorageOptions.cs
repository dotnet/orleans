using Orleans.Core;

namespace Orleans.Persistence.Cosmos;

/// <summary>
/// Options for Azure Cosmos DB grain persistence.
/// </summary>
public class CosmosGrainStorageOptions : CosmosOptions
{
    private const string ORLEANS_STORAGE_CONTAINER = "OrleansStorage";
    public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;
    internal const string DEFAULT_PARTITION_KEY_PATH = "/PartitionKey";

    /// <summary>
    /// Stage of silo lifecycle where storage should be initialized. Storage must be initialized prior to use.
    /// </summary>
    public int InitStage { get; set; } = DEFAULT_INIT_STAGE;

    /// <summary>
    /// Gets or sets a value indicating whether state should be deleted when <see cref="IStorage.ClearStateAsync()"/> is called.
    /// </summary>
    public bool DeleteStateOnClear { get; set; }

    /// <summary>
    /// List of JSON path strings.
    /// Each entry on this list represents a property in the State Object that will be included in the document index.
    /// The default is to not add any property in the State object.
    /// </summary>
    public List<string> StateFieldsToIndex { get; set; } = new();

    /// <summary>
    /// Gets or sets the partition-key path used when <see cref="PartitionKeyLevelCount"/> is <c>1</c>.
    /// </summary>
    public string PartitionKeyPath { get; set; } = DEFAULT_PARTITION_KEY_PATH;

    /// <summary>
    /// Gets or sets the number of partition-key levels. The supported values are <c>1</c>, <c>2</c>, and <c>3</c>.
    /// The default is <c>1</c>.
    /// </summary>
    /// <remarks>
    /// Values greater than <c>1</c> use <c>/PartitionKey</c>, <c>/PartitionKey2</c>, and <c>/PartitionKey3</c>, in order.
    /// </remarks>
    public int PartitionKeyLevelCount { get; set; } = 1;

    /// <summary>
    /// Initializes a new <see cref="CosmosGrainStorageOptions"/> instance.
    /// </summary>
    public CosmosGrainStorageOptions()
    {
        ContainerName = ORLEANS_STORAGE_CONTAINER;
    }
}
