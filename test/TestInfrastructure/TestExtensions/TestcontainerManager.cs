using Docker.DotNet;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace TestExtensions;

/// <summary>
/// Lazily starts a shared test container and skips tests when a compatible Docker daemon is unavailable.
/// </summary>
public sealed class TestcontainerManager<TContainer> where TContainer : IContainer
{
    private static readonly Lazy<string?> DockerDaemonOsTypeLazy = new(GetDockerDaemonOsType);

    private readonly string _serviceName;
    private readonly Lazy<TContainer> _container;
    private readonly Action<TContainer>? _onStarted;
    private readonly Lazy<string?> _startSkipReason;

    public TestcontainerManager(string serviceName, Func<TContainer> containerFactory, Action<TContainer>? onStarted = null)
    {
        _serviceName = serviceName;
        _container = new(containerFactory);
        _onStarted = onStarted;
        _startSkipReason = new(() => StartAndGetSkipReasonAsync().GetAwaiter().GetResult());
    }

    public TContainer Container
    {
        get
        {
            EnsureStarted();
            return _container.Value;
        }
    }

    public void EnsureStarted()
    {
        var skipReason = _startSkipReason.Value;
        if (skipReason is not null)
            throw new SkipException(skipReason);
    }

    public Task<bool> EnsureStartedAsync() => Task.FromResult(_startSkipReason.Value is null);

    private async Task<string?> StartAndGetSkipReasonAsync()
    {
        var dockerDaemonOsType = DockerDaemonOsTypeLazy.Value;
        if (string.IsNullOrWhiteSpace(dockerDaemonOsType))
            return GetDockerUnavailableSkipReason();

        if (string.Equals(dockerDaemonOsType, "windows", StringComparison.OrdinalIgnoreCase))
            return $"Docker is running in Windows container mode, so {_serviceName} tests are skipped.";

        try
        {
            var container = _container.Value;
            await container.StartAsync();
            _onStarted?.Invoke(container);
            return null;
        }
        catch (DockerUnavailableException exception)
        {
            return GetDockerUnavailableSkipReason(exception);
        }
        catch (HttpRequestException exception)
        {
            return GetDockerUnavailableSkipReason(exception);
        }
        catch (OperationCanceledException exception)
        {
            return GetDockerUnavailableSkipReason(exception);
        }
        catch (DockerApiException exception)
        {
            return GetDockerUnavailableSkipReason(exception);
        }
    }

    private string GetDockerUnavailableSkipReason(Exception? exception = null)
    {
        var reason = $"Docker is unavailable, so {_serviceName} tests are skipped.";
        return exception is null ? reason : $"{reason} {exception.Message}";
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
        catch (DockerUnavailableException)
        {
            return null;
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
