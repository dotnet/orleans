using Microsoft.Extensions.Logging.Abstractions;
using TestExtensions;
using Xunit;

namespace UnitTests.TimerTests;

[TestSuite("BVT")]
[TestProvider("None")]
public class ReminderLifecycleHarnessTests
{
    [Fact, TestCategory("BVT")]
    public async Task CleanupUsesIndependentTokenAfterTestCancellation()
    {
        using var testCancellation = new CancellationTokenSource();
        testCancellation.Cancel();
        var phases = new List<string>();

        await ReminderTestsBase.ExecuteCleanupAsync(
            cancellationToken =>
            {
                Assert.NotEqual(testCancellation.Token, cancellationToken);
                Assert.False(cancellationToken.IsCancellationRequested);
                phases.Add("clear");
                return Task.CompletedTask;
            },
            cancellationToken =>
            {
                Assert.NotEqual(testCancellation.Token, cancellationToken);
                Assert.False(cancellationToken.IsCancellationRequested);
                phases.Add("refresh");
                return Task.CompletedTask;
            },
            cancellationToken =>
            {
                Assert.NotEqual(testCancellation.Token, cancellationToken);
                Assert.False(cancellationToken.IsCancellationRequested);
                phases.Add("quiescence");
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(1));

        Assert.Equal(["clear", "refresh", "quiescence"], phases);
    }

    [Fact, TestCategory("BVT")]
    public async Task PartialStartupCleanupStopsDiscoveredResourcesWhenStartupStalls()
    {
        var initialResources = new HashSet<string> { "initial" };
        IReadOnlyList<string> activeResources = ["initial", "partially-started"];
        var stoppedResources = new List<string>();
        var topologyReconciled = false;
        var stalledStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startupWaitCancellation = new CancellationTokenSource();
        startupWaitCancellation.Cancel();

        await ReminderLifecycleHarness.CleanupPartialStartupAsync(
            initialResources,
            stalledStartup.Task,
            () => activeResources,
            resource =>
            {
                stoppedResources.Add(resource);
                return Task.CompletedTask;
            },
            () =>
            {
                topologyReconciled = true;
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            startupWaitCancellation.Token,
            TestContext.Current.CancellationToken);

        Assert.Equal(["partially-started"], stoppedResources);
        Assert.True(topologyReconciled);
    }
}
