using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Orleans.TestingHost;
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
    public async Task CleanupDoesNotStartAnotherPhaseAfterBudgetExpires()
    {
        var phases = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReminderTestsBase.ExecuteCleanupAsync(
                async cancellationToken =>
                {
                    phases.Add("clear");
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                },
                _ =>
                {
                    phases.Add("refresh");
                    return Task.CompletedTask;
                },
                _ =>
                {
                    phases.Add("quiescence");
                    return Task.CompletedTask;
                },
                TimeSpan.Zero));

        Assert.Equal(["clear"], phases);
    }

    [Fact, TestCategory("BVT")]
    public async Task PhaseWaitPreservesExternalCancellation()
    {
        using var timeoutCancellation = new CancellationTokenSource();
        using var externalCancellation = new CancellationTokenSource();
        externalCancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReminderLifecycleHarness.WaitForPhaseAsync(
                cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                timeoutCancellation.Token,
                externalCancellation.Token,
                () => new TimeoutException("dedicated timeout")));

        Assert.IsNotType<TimeoutException>(exception);
        Assert.True(exception.CancellationToken.IsCancellationRequested);
    }

    [Fact, TestCategory("BVT")]
    public async Task PhaseWaitTranslatesDedicatedTimeout()
    {
        using var timeoutCancellation = new CancellationTokenSource();
        timeoutCancellation.Cancel();

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            ReminderLifecycleHarness.WaitForPhaseAsync(
                cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                timeoutCancellation.Token,
                TestContext.Current.CancellationToken,
                () => new TimeoutException("dedicated timeout")));

        Assert.Equal("dedicated timeout", exception.Message);
    }

    [Fact, TestCategory("BVT")]
    public async Task PartialStartupCannotPublishSiloAfterCleanupReturns()
    {
        var controller = new ControlledSiloStartup();
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureSilo((siloOptions, siloBuilder) =>
            siloBuilder.Services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(
                new ControlledSiloStartupParticipant(siloOptions.SiloName, controller)));
        await using var cluster = builder.Build();
        using var testCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        testCancellation.CancelAfter(TestConstants.InitTimeout);
        await cluster.DeployAsync(testCancellation.Token);

        var initialSilos = cluster.GetActiveSilos().ToHashSet();
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellation.Token);
        var startupBlocked = controller.BlockNextStartup("Silo_2");
        var startupTask = cluster.StartSilosAsync(2, startupCancellation.Token);
        await startupBlocked.WaitAsync(testCancellation.Token);

        using var cleanupCancellation = new CancellationTokenSource(TestConstants.InitTimeout);
        await ReminderLifecycleHarness.CleanupPartialStartupAsync(
            initialSilos,
            startupTask,
            () => cluster.GetActiveSilos().ToArray(),
            cluster.StopSiloAsync,
            () => cluster.WaitForLivenessToStabilizeAsync(didKill: true),
            NullLogger.Instance,
            startupCancellation,
            cleanupCancellation.Token);

        Assert.True(startupTask.IsCompleted);
        Assert.Equal(initialSilos, cluster.GetActiveSilos().ToHashSet());
    }

    private sealed class ControlledSiloStartup
    {
        private readonly object _lock = new();
        private string? _siloName;
        private TaskCompletionSource? _blocked;

        public Task BlockNextStartup(string siloName)
        {
            lock (_lock)
            {
                if (_siloName is not null)
                {
                    throw new InvalidOperationException($"Startup for {_siloName} is already blocked.");
                }

                _siloName = siloName;
                _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return _blocked.Task;
            }
        }

        public async Task OnStartAsync(string siloName, CancellationToken cancellationToken)
        {
            TaskCompletionSource? blocked;
            lock (_lock)
            {
                if (_siloName != siloName)
                {
                    return;
                }

                _siloName = null;
                blocked = _blocked;
                _blocked = null;
            }

            blocked!.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ControlledSiloStartupParticipant(
        string siloName,
        ControlledSiloStartup controller) : ILifecycleParticipant<ISiloLifecycle>
    {
        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(
                nameof(ControlledSiloStartupParticipant),
                ServiceLifecycleStage.Active,
                cancellationToken => controller.OnStartAsync(siloName, cancellationToken));
        }
    }
}
