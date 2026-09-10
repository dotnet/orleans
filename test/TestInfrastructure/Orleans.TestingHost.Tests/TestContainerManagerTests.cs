using TestExtensions;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
public class TestContainerManagerTests
{
    [Fact]
    public async Task ConcurrentCallersStartContainerAndPublishConnectionOnce()
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
            () => Task.FromResult<string?>(null));

        var callers = Enumerable.Range(0, 8).Select(_ => manager.EnsureStartedAsync()).ToArray();
        await startEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        releaseStart.SetResult();

        Assert.All(await Task.WhenAll(callers), Assert.True);
        Assert.Equal(1, startCalls);
        Assert.Equal("connection", publishedConnection);
        Assert.Same(container, manager.Container);
    }

    [Fact]
    public async Task DockerSkipReasonPreventsContainerCreation()
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
            getDockerSkipReasonAsync: () => Task.FromResult<string?>("Docker is unavailable."));

        Assert.False(await manager.EnsureStartedAsync());
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task StartupFailureIsPropagated()
    {
        var expected = new InvalidOperationException("Container startup failed.");
        var manager = CreateManager((_, _) => Task.FromException(expected));

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureStartedAsync());

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task StartupCancellationIsPropagated()
    {
        var expected = new OperationCanceledException("Container startup timed out.");
        var manager = CreateManager((_, _) => Task.FromException(expected));

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.EnsureStartedAsync());

        Assert.Same(expected, actual);
    }

    private static TestContainerManager<object> CreateManager(Func<object, CancellationToken, Task> startAsync)
    {
        return new(
            "Test service",
            static () => new(),
            startAsync,
            getDockerSkipReasonAsync: () => Task.FromResult<string?>(null));
    }
}
