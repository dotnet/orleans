using Orleans.AzureCosmos;

namespace Orleans.Configuration
{
    /// <summary>
    /// Specify options used for Azure Cosmos DB reminder storage
    /// </summary>
    public class AzureCosmosReminderOptions : StorageOptionsBase
    {
        public AzureCosmosReminderOptions() => ContainerName = DEFAULT_CONTAINER_NAME;
        public const string DEFAULT_CONTAINER_NAME = "OrleansReminders";
    }
}
