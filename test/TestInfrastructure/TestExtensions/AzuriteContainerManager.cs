using Testcontainers.Azurite;

namespace TestExtensions;

/// <summary>
/// Provides one process-shared Azurite container for Azure Storage integration tests.
/// </summary>
public static class AzuriteContainerManager
{
    private const string ConnectionStringEnvVar = "ORLEANS_AZURITE_CONNECTION_STRING";
    private static readonly TestContainerManager<AzuriteContainer> ContainerManager = new(
        "Azure Storage",
        CreateContainer,
        static (container, cancellationToken) => container.StartAsync(cancellationToken),
        container => Environment.SetEnvironmentVariable(ConnectionStringEnvVar, container.GetConnectionString()));

    /// <summary>
    /// Gets the shared Azurite connection string, including the value inherited by standalone silo processes.
    /// </summary>
    public static string ConnectionString
    {
        get
        {
            var envConnectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);
            if (!string.IsNullOrEmpty(envConnectionString))
            {
                return envConnectionString;
            }

            return ContainerManager.Container.GetConnectionString();
        }
    }

    /// <summary>
    /// Starts the shared Azurite container or uses the connection inherited by a standalone silo process.
    /// </summary>
    /// <exception cref="Xunit.Sdk.SkipException">Thrown when Docker cannot host the Azurite container.</exception>
    public static void EnsureStarted()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ConnectionStringEnvVar)))
        {
            return;
        }

        ContainerManager.EnsureStarted();
    }

    private static AzuriteContainer CreateContainer()
    {
        return new AzuriteBuilder(
            "mcr.microsoft.com/azure-storage/azurite:3.35.0@sha256:647c63a91102a9d8e8000aab803436e1fc85fbb285e7ce830a82ee5d6661cf37")
            .WithCommand("--skipApiVersionCheck")
            .Build();
    }
}
