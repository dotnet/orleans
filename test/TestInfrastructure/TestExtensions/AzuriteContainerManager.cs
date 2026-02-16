using Testcontainers.Azurite;
using Xunit;

namespace TestExtensions;

/// <summary>
/// Manages a singleton Azurite container for Azure Storage integration tests.
/// The container is lazily started on first use and shared across all tests in the process.
/// </summary>
public static class AzuriteContainerManager
{
    private static readonly AzuriteContainer _container = new AzuriteBuilder().Build();

    /// <summary>
    /// Gets the connection string for the running Azurite container.
    /// </summary>
    public static string ConnectionString
    {
        get
        {
            EnsureStarted();
            return _container.GetConnectionString();
        }
    }

    private static readonly Lazy<bool> _ensureStartedLazy = new(() => EnsureStartedAsync().Result);

    /// <summary>
    /// Ensures Azurite is available by starting the container if not already running.
    /// </summary>
    /// <exception cref="SkipException">Thrown if the container could not be started.</exception>
    public static void EnsureStarted()
    {
        if (!_ensureStartedLazy.Value)
            throw new SkipException("Azurite container could not be started. Skipping.");
    }

    /// <summary>
    /// Ensures Azurite is available by starting an Azurite Testcontainer.
    /// The container is started once and shared across all tests in the process.
    /// </summary>
    /// <returns><see langword="true"/> if Azurite is available; <see langword="false"/> if it could not be started.</returns>
    public static async Task<bool> EnsureStartedAsync()
    {
        try
        {
            await _container.StartAsync();
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
