using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Reminders.TestKit;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.TimerTests;

/// <summary>
/// Exposes the shared reminder-service lifecycle contract without inheriting provider whole-table cleanup.
/// </summary>
[Collection(TestEnvironmentFixture.DefaultCollection)]
public abstract class ReminderServiceLifecycleTestsBase
{
    private static readonly TimeSpan ScenarioTimeout = TimeSpan.FromMinutes(2);
    private readonly ReminderServiceLifecycleTestRunner _runner;

    protected ReminderServiceLifecycleTestsBase(
        ReminderTestClock reminderClock,
        InProcessTestCluster cluster,
        string providerName)
    {
        ArgumentNullException.ThrowIfNull(reminderClock);
        ArgumentNullException.ThrowIfNull(cluster);
        var services = cluster.GetActiveSilos().First().ServiceProvider;
        var options = services.GetRequiredService<IOptions<ReminderOptions>>().Value;
        var harness = new ReminderServiceLifecycleHarness(
            cluster,
            reminderClock,
            reminderClock.DiagnosticObserver,
            options.ReminderLoadingWindow);
        _runner = new ProviderRunner(harness, providerName);
    }

    [Fact]
    public Task ReminderService_StartupReadiness()
        => RunAsync(_runner.RunReminderService_StartupReadiness);

    [Fact]
    public Task ReminderService_RegistrationHasSingleOwner()
        => RunAsync(_runner.RunReminderService_RegistrationHasSingleOwner);

    [Fact]
    public Task ReminderService_UpdateDoesNotRestartLocalOwner()
        => RunAsync(_runner.RunReminderService_UpdateDoesNotRestartLocalOwner);

    [Fact]
    public Task ReminderService_RemovalReachesQuiescence()
        => RunAsync(_runner.RunReminderService_RemovalReachesQuiescence);

    [Fact]
    public Task ReminderService_ExactDueRecovery()
        => RunAsync(_runner.RunReminderService_ExactDueRecovery);

    [Fact]
    public Task ReminderService_StaleOwnerRegistrationReconciles()
        => RunAsync(_runner.RunReminderService_StaleOwnerRegistrationReconciles);

    [Fact]
    public Task ReminderService_OneSiloJoinLeaveTransfersOwnership()
        => RunAsync(_runner.RunReminderService_OneSiloJoinLeaveTransfersOwnership);

    [Fact]
    public Task ReminderService_CleanupIsIsolated()
        => RunAsync(_runner.RunReminderService_CleanupIsIsolated);

    private static async Task RunAsync(Func<CancellationToken, Task> scenario)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.CancelAfter(ScenarioTimeout);
        await scenario(cancellation.Token);
    }

    private sealed class ProviderRunner(IReminderServiceLifecycleHarness harness, string providerName)
        : ReminderServiceLifecycleTestRunner(harness, providerName);
}
