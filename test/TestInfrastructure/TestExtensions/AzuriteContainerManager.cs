using Docker.DotNet;
using DotNet.Testcontainers.Configurations;
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
    private const string DockerUnavailableSkipReason = "Docker is unavailable, so Azure Storage tests are skipped.";
    private const string WindowsDockerModeSkipReason = "Docker is running in Windows container mode, so Azure Storage tests are skipped.";

    private static readonly AzuriteContainer _container = new AzuriteBuilder(
        "mcr.microsoft.com/azure-storage/azurite:3.35.0@sha256:647c63a91102a9d8e8000aab803436e1fc85fbb285e7ce830a82ee5d6661cf37")
        .WithCommand("--skipApiVersionCheck")
        .Build();

    private static readonly Lazy<string?> DockerDaemonOsTypeLazy = new(GetDockerDaemonOsType);
    private static readonly Lazy<string?> EnsureStartedSkipReasonLazy = new(() => EnsureStartedAndGetSkipReasonAsync().GetAwaiter().GetResult());

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

        var skipReason = EnsureStartedSkipReasonLazy.Value;
        if (skipReason is not null)
            throw new SkipException(skipReason);
    }

    /// <summary>
    /// Ensures Azurite is available by starting an Azurite Testcontainer.
    /// The container is started once and shared across all tests in the process.
    /// The connection string is propagated to child processes via environment variable.
    /// </summary>
    /// <returns><see langword="true"/> if Azurite is available; <see langword="false"/> if it could not be started.</returns>
    public static async Task<bool> EnsureStartedAsync()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ConnectionStringEnvVar)))
            return true;

        return await EnsureStartedAndGetSkipReasonAsync() is null;
    }

    private static async Task<string?> EnsureStartedAndGetSkipReasonAsync()
    {
        var skipReason = GetDockerSkipReason();
        if (skipReason is not null)
            return skipReason;

        try
        {
            await _container.StartAsync();
            Environment.SetEnvironmentVariable(ConnectionStringEnvVar, _container.GetConnectionString());
            return null;
        }
        catch (HttpRequestException exception)
        {
            return $"{DockerUnavailableSkipReason} {exception.Message}";
        }
        catch (OperationCanceledException exception)
        {
            return $"{DockerUnavailableSkipReason} {exception.Message}";
        }
        catch (DockerApiException exception)
        {
            return $"{DockerUnavailableSkipReason} {exception.Message}";
        }
    }

    private static string? GetDockerSkipReason()
    {
        var dockerDaemonOsType = DockerDaemonOsTypeLazy.Value;
        if (string.IsNullOrWhiteSpace(dockerDaemonOsType))
            return DockerUnavailableSkipReason;

        return string.Equals(dockerDaemonOsType, "windows", StringComparison.OrdinalIgnoreCase)
            ? WindowsDockerModeSkipReason
            : null;
    }

    private static string? GetDockerDaemonOsType()
    {
        try
        {
            using var dockerClient = TestcontainersSettings.OS.DockerEndpointAuthConfig
                .GetDockerClientConfiguration(Guid.NewGuid())
                .CreateClient();
            var dockerInfo = dockerClient.System.GetSystemInfoAsync().GetAwaiter().GetResult();
            return dockerInfo.OSType;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (DockerApiException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
