using System.Net;
using Docker.DotNet;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Time.Testing;
using TestExtensions;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
public class TestContainerManagerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCallersStartContainerAndPublishConnectionOnce(bool isContinuousIntegration)
    {
        var container = new object();
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startCalls = 0;
        var publishedConnection = "";
        var manager = new TestContainerManager<object>(
            "Test service",
            () => container,
            async (value, _) =>
            {
                Assert.Same(container, value);
                Interlocked.Increment(ref startCalls);
                startEntered.TrySetResult();
                await releaseStart.Task;
            },
            _ => publishedConnection = "connection",
            () => Task.FromResult<string?>(null),
            isContinuousIntegration);

        var callers = Enumerable.Range(0, 8).Select(_ => manager.EnsureStartedAsync()).ToArray();
        await startEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        releaseStart.SetResult();

        Assert.All(await Task.WhenAll(callers), Assert.True);
        Assert.Equal(1, startCalls);
        Assert.Equal("connection", publishedConnection);
        Assert.Same(container, manager.Container);
    }

    [Fact]
    public async Task UnavailableLocalDockerSkipsWithoutCreatingContainer()
    {
        var factoryCalls = 0;
        var manager = new TestContainerManager<object>(
            "Test service",
            () =>
            {
                factoryCalls++;
                return new();
            },
            static (_, _) => Task.CompletedTask,
            getDockerSkipReasonAsync: () => Task.FromResult<string?>("Docker is unavailable."),
            isContinuousIntegration: false);

        Assert.False(await manager.EnsureStartedAsync());
        Xunit.Sdk.SkipException? exception = null;
        try
        {
            manager.EnsureStarted();
        }
        catch (Xunit.Sdk.SkipException caught)
        {
            exception = caught;
        }

        Assert.NotNull(exception);
        Assert.Contains("Docker is unavailable. Test service tests are skipped.", exception.Message);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task UnavailableCiDockerFailsWithoutCreatingContainer()
    {
        var factoryCalls = 0;
        var manager = new TestContainerManager<object>(
            "Test service",
            () =>
            {
                factoryCalls++;
                return new();
            },
            static (_, _) => Task.CompletedTask,
            getDockerSkipReasonAsync: () => Task.FromResult<string?>("Docker is unavailable."),
            isContinuousIntegration: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureStartedAsync());

        Assert.Equal("Test service tests require Linux Docker in CI. Docker is unavailable.", exception.Message);
        Assert.Same(exception, Assert.Throws<InvalidOperationException>(manager.EnsureStarted));
        Assert.Equal(0, factoryCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupTokenCancelsAfterFiveMinutesAndFailureIsCached(bool isContinuousIntegration)
    {
        var timeProvider = new FakeTimeProvider();
        var startCalls = 0;
        var publishCalls = 0;
        var startupToken = CancellationToken.None;
        var manager = new TestContainerManager<object>(
            "Test service",
            static () => new(),
            (_, cancellationToken) =>
            {
                startCalls++;
                startupToken = cancellationToken;
                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            _ => publishCalls++,
            getDockerSkipReasonAsync: () => Task.FromResult<string?>(null),
            isContinuousIntegration: isContinuousIntegration,
            timeProvider: timeProvider);

        var firstCaller = manager.EnsureStartedAsync();
        var secondCaller = manager.EnsureStartedAsync();
        Assert.True(startupToken.CanBeCanceled);

        timeProvider.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        Assert.False(startupToken.IsCancellationRequested);
        Assert.False(firstCaller.IsCompleted);
        Assert.False(secondCaller.IsCompleted);

        timeProvider.Advance(TimeSpan.FromTicks(1));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstCaller);
        var secondException = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondCaller);
        Assert.Equal(startupToken, exception.CancellationToken);
        Assert.Same(exception, secondException);
        Assert.Same(exception, Record.Exception(manager.EnsureStarted));
        Assert.True(startupToken.IsCancellationRequested);
        Assert.Equal(1, startCalls);
        Assert.Equal(0, publishCalls);
    }

    [Theory]
    [InlineData(false, "configuration")]
    [InlineData(true, "configuration")]
    [InlineData(false, "cancellation")]
    [InlineData(true, "cancellation")]
    [InlineData(false, "http")]
    [InlineData(true, "http")]
    [InlineData(false, "docker-api")]
    [InlineData(true, "docker-api")]
    [InlineData(false, "docker-unavailable")]
    [InlineData(true, "docker-unavailable")]
    public async Task StartupFailureIsPropagatedAndCached(bool isContinuousIntegration, string failureKind)
    {
        Exception expected = failureKind switch
        {
            "configuration" => new InvalidOperationException("Container configuration failed."),
            "cancellation" => new OperationCanceledException("Container startup timed out."),
            "http" => new HttpRequestException("Image download failed."),
            "docker-api" => new DockerApiException(HttpStatusCode.InternalServerError, "Container creation failed."),
            "docker-unavailable" => new DockerUnavailableException("Docker stopped after the availability check."),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
        };
        var startCalls = 0;
        var publishCalls = 0;
        var manager = new TestContainerManager<object>(
            "Test service",
            static () => new(),
            (_, _) =>
            {
                startCalls++;
                return Task.FromException(expected);
            },
            _ => publishCalls++,
            getDockerSkipReasonAsync: () => Task.FromResult<string?>(null),
            isContinuousIntegration: isContinuousIntegration);

        var actual = await Record.ExceptionAsync(() => manager.EnsureStartedAsync());

        Assert.Same(expected, actual);
        Assert.Same(expected, Record.Exception(manager.EnsureStarted));
        Assert.Equal(1, startCalls);
        Assert.Equal(0, publishCalls);
    }
}
