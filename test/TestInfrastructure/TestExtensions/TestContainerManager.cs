using Docker.DotNet;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;

namespace TestExtensions;

/// <summary>
/// Starts one shared test container and reports Docker availability to test fixtures.
/// </summary>
internal sealed class TestContainerManager<TContainer>
{
    private static readonly Lazy<Task<string?>> DockerSkipReason = new(GetDockerSkipReasonAsync);

    private readonly string _serviceName;
    private readonly Lazy<TContainer> _container;
    private readonly Func<TContainer, CancellationToken, Task> _startAsync;
    private readonly Func<Task<string?>> _getDockerSkipReasonAsync;
    private readonly Action<TContainer>? _onStarted;
    private readonly Lazy<Task<string?>> _startSkipReason;

    public TestContainerManager(
        string serviceName,
        Func<TContainer> containerFactory,
        Func<TContainer, CancellationToken, Task> startAsync,
        Action<TContainer>? onStarted = null,
        Func<Task<string?>>? getDockerSkipReasonAsync = null)
    {
        _serviceName = serviceName;
        _container = new(containerFactory);
        _startAsync = startAsync;
        _getDockerSkipReasonAsync = getDockerSkipReasonAsync ?? (() => DockerSkipReason.Value);
        _onStarted = onStarted;
        _startSkipReason = new(StartAndGetSkipReasonAsync);
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
        var skipReason = _startSkipReason.Value.GetAwaiter().GetResult();
        if (skipReason is not null)
        {
            throw Xunit.Sdk.SkipException.ForSkip(skipReason);
        }
    }

    public async Task<bool> EnsureStartedAsync() => await _startSkipReason.Value.ConfigureAwait(false) is null;

    private async Task<string?> StartAndGetSkipReasonAsync()
    {
        var dockerSkipReason = await _getDockerSkipReasonAsync().ConfigureAwait(false);
        if (dockerSkipReason is not null)
        {
            return $"{dockerSkipReason} {_serviceName} tests are skipped.";
        }

        var container = _container.Value;
        await _startAsync(container, CancellationToken.None).ConfigureAwait(false);
        _onStarted?.Invoke(container);
        return null;
    }

    private static async Task<string?> GetDockerSkipReasonAsync()
    {
        try
        {
            using var dockerClient = TestcontainersSettings.OS.DockerEndpointAuthConfig
                .GetDockerClientConfiguration(Guid.NewGuid())
                .CreateClient();
            var dockerInfo = await dockerClient.System.GetSystemInfoAsync().ConfigureAwait(false);
            return string.Equals(dockerInfo.OSType, "windows", StringComparison.OrdinalIgnoreCase)
                ? "Docker is running in Windows container mode."
                : null;
        }
        catch (DockerUnavailableException exception)
        {
            return $"Docker is unavailable. {exception.Message}";
        }
        catch (HttpRequestException exception)
        {
            return $"Docker is unavailable. {exception.Message}";
        }
        catch (OperationCanceledException exception)
        {
            return $"Docker is unavailable. {exception.Message}";
        }
        catch (DockerApiException exception)
        {
            return $"Docker is unavailable. {exception.Message}";
        }
        catch (InvalidOperationException exception)
        {
            return $"Docker is unavailable. {exception.Message}";
        }
    }
}
