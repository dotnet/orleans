namespace Orleans.Journaling;

public class LogStorageOptions : CosmosOptions
{
    private const string CONTAINER_NAME = "OrleansJournaling";

    public LogStorageOptions()
    {
        ContainerName = CONTAINER_NAME;
    }
}
