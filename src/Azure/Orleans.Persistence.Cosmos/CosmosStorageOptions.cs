using Orleans.Core;

namespace Orleans.Persistence.Cosmos;

/// <summary>
/// Options for Azure Cosmos DB grain persistence.
/// </summary>
public class CosmosGrainStorageOptions : CosmosOptions
{
    private const string ORLEANS_STORAGE_CONTAINER = "OrleansStorage";
    public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;
    private const string DEFAULT_PARTITION_KEY_PATH = "/PartitionKey";

    /// <summary>
    /// Stage of silo lifecycle where storage should be initialized. Storage must be initialized prior to use.
    /// </summary>
    public int InitStage { get; set; } = DEFAULT_INIT_STAGE;

    /// <summary>
    /// Gets or sets a value indicating whether state should be deleted when <see cref="IStorage.ClearStateAsync"/> is called.
    /// </summary>
    public bool DeleteStateOnClear { get; set; }

    /// <summary>
    /// List of JSON path strings.
    /// Each entry on this list represents a property in the State Object that will be included in the document index.
    /// The default is to not add any property in the State object.
    /// </summary>
    public List<string> StateFieldsToIndex { get; set; } = new();

    public string PartitionKeyPath { get; set; } = DEFAULT_PARTITION_KEY_PATH;

    /// <summary>
    /// Initializes a new <see cref="CosmosGrainStorageOptions"/> instance.
    /// </summary>
    public CosmosGrainStorageOptions()
    {
        ContainerName = ORLEANS_STORAGE_CONTAINER;
    }
}
