using Testcontainers.Azurite;

namespace TestExtensions;

/// <summary>
/// Manages a singleton Azurite container for Azure Storage integration tests.
/// The container is lazily started on first use and shared across all tests in the process.
/// </summary>
public static class AzuriteContainer
{
    private static Testcontainers.Azurite.AzuriteContainer _container;
    private static string _connectionString;
    private static readonly object _lock = new();
    private static Task<bool> _startTask;

    /// <summary>
    /// Gets the connection string for the running Azurite container.
    /// Returns <see langword="null"/> if the container has not been started.
    /// </summary>
    public static string ConnectionString => _connectionString;

    /// <summary>
    /// Ensures Azurite is available by starting an Azurite Testcontainer.
    /// The container is started once and shared across all tests in the process.
    /// </summary>
    /// <returns><see langword="true"/> if Azurite is available; <see langword="false"/> if it could not be started.</returns>
    public static Task<bool> EnsureStartedAsync()
    {
        if (_startTask is not null)
        {
            return _startTask;
        }

        lock (_lock)
        {
            _startTask ??= StartAsync();
        }

        return _startTask;
    }

    private static async Task<bool> StartAsync()
    {
        try
        {
            var container = new AzuriteBuilder()
                .WithInMemoryPersistence()
                .Build();

            await container.StartAsync();

            _connectionString = container.GetConnectionString();
            _container = container;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
