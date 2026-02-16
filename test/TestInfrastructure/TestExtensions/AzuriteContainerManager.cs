using Testcontainers.Azurite;
using Xunit;

namespace TestExtensions;

/// <summary>
/// Manages a singleton Azurite container for Azure Storage integration tests.
/// The container is lazily started on first use and shared across all tests in the process.
/// When running standalone silos (separate processes), the connection string is propagated
/// via environment variable so child processes reuse the same container.
/// </summary>
public static class AzuriteContainerManager
{
    private const string ConnectionStringEnvVar = "ORLEANS_AZURITE_CONNECTION_STRING";

    private static readonly AzuriteContainer _container = new AzuriteBuilder()
        .WithImage("mcr.microsoft.com/azure-storage/azurite:3.35.0")
        .Build();

    /// <summary>
    /// Gets the connection string for the running Azurite container.
    /// If running in a child process (e.g. standalone silo), returns the connection string
    /// propagated via environment variable without starting a new container.
    /// </summary>
    public static string ConnectionString
    {
        get
        {
            var envConnectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);
            if (!string.IsNullOrEmpty(envConnectionString))
                return envConnectionString;

            EnsureStarted();
            return _container.GetConnectionString();
        }
    }

    private static readonly Lazy<bool> _ensureStartedLazy = new(() => EnsureStartedAsync().Result);

    /// <summary>
    /// Ensures Azurite is available by starting the container if not already running.
    /// If the connection string is already available via environment variable (e.g. in a child process),
    /// this method is a no-op.
    /// </summary>
    /// <exception cref="SkipException">Thrown if the container could not be started.</exception>
    public static void EnsureStarted()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ConnectionStringEnvVar)))
            return;

        if (!_ensureStartedLazy.Value)
            throw new SkipException("Azurite container could not be started. Skipping.");
    }

    /// <summary>
    /// Ensures Azurite is available by starting an Azurite Testcontainer.
    /// The container is started once and shared across all tests in the process.
    /// The connection string is propagated to child processes via environment variable.
    /// </summary>
    /// <returns><see langword="true"/> if Azurite is available; <see langword="false"/> if it could not be started.</returns>
    public static async Task<bool> EnsureStartedAsync()
    {
        try
        {
            await _container.StartAsync();
            Environment.SetEnvironmentVariable(ConnectionStringEnvVar, _container.GetConnectionString());
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
