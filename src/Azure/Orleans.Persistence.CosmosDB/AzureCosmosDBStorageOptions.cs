namespace Orleans.Persistence.CosmosDB;

public class AzureCosmosDBStorageOptions : AzureCosmosDBOptions
{
    private const string ORLEANS_STORAGE_CONTAINER = "OrleansStorage";
    public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

    /// <summary>
    /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialized prior to use.
    /// </summary>
    public int InitStage { get; set; } = DEFAULT_INIT_STAGE;

    public bool DeleteStateOnClear { get; set; }

    /// <summary>
    /// List of JSON path strings.
    /// Each entry on this list represents a property in the State Object that will be included in the document index.
    /// The default is to not add any property in the State object.
    /// </summary>
    public List<string> StateFieldsToIndex { get; set; } = new();

    public AzureCosmosDBStorageOptions()
    {
        this.Container = ORLEANS_STORAGE_CONTAINER;
    }
}
