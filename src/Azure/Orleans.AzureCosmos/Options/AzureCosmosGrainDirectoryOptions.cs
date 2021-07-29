using Orleans.AzureCosmos;

namespace Orleans.Configuration
{
    public class AzureCosmosGrainDirectoryOptions : StorageOptionsBase
    {
        public AzureCosmosGrainDirectoryOptions() => ContainerName = DEFAULT_CONTAINER_NAME;
        public const string DEFAULT_CONTAINER_NAME = "GrainDirectory";
    }
}
