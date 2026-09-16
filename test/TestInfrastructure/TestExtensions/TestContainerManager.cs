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
    private readonly bool _isContinuousIntegration;
    private readonly TimeProvider _timeProvider;
    private readonly Lazy<Task<string?>> _startSkipReason;

    public TestContainerManager(
        string serviceName,
        Func<TContainer> containerFactory,
        Func<TContainer, CancellationToken, Task> startAsync,
        Action<TContainer>? onStarted = null,
        Func<Task<string?>>? getDockerSkipReasonAsync = null,
        bool? isContinuousIntegration = null,
        TimeProvider? timeProvider = null)
    {
        _serviceName = serviceName;
        _container = new(containerFactory);
        _startAsync = startAsync;
        _getDockerSkipReasonAsync = getDockerSkipReasonAsync ?? (() => DockerSkipReason.Value);
        _onStarted = onStarted;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _isContinuousIntegration = isContinuousIntegration
            ?? (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Environment.GetEnvironmentVariable("TF_BUILD"), "true", StringComparison.OrdinalIgnoreCase));
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
            if (_isContinuousIntegration)
            {
                throw new InvalidOperationException($"{_serviceName} tests require Linux Docker in CI. {dockerSkipReason}");
            }

            return $"{dockerSkipReason} {_serviceName} tests are skipped.";
        }

        var container = _container.Value;
        using var startupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5), _timeProvider);
        await _startAsync(container, startupTimeout.Token).ConfigureAwait(false);

        _onStarted?.Invoke(container);
        return null;
    }

    private static async Task<string?> GetDockerSkipReasonAsync()
    {
        try
        {
            var endpointAuthConfig = TestcontainersSettings.OS.DockerEndpointAuthConfig;
            if (endpointAuthConfig is null)
            {
                return "Docker endpoint configuration is unavailable.";
            }

            using var dockerClient = endpointAuthConfig
                .GetDockerClientConfiguration(Guid.NewGuid())
                .CreateClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var dockerInfo = await dockerClient.System.GetSystemInfoAsync(timeout.Token).ConfigureAwait(false);
            return string.Equals(dockerInfo.OSType, "windows", StringComparison.OrdinalIgnoreCase)
                ? "Docker is running in Windows container mode."
                : null;
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
        catch (InvalidOperationException exception)
        {
            return GetDockerUnavailableSkipReason(exception);
        }
    }

    private static string GetDockerUnavailableSkipReason(Exception exception) => $"Docker is unavailable. {exception.Message}";
}
