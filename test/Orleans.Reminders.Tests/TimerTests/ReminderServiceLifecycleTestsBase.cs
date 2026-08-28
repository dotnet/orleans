using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Reminders.TestKit;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.TimerTests;

/// <summary>
/// Exposes the shared reminder-service lifecycle contract without inheriting provider whole-table cleanup.
/// </summary>
public abstract class ReminderServiceLifecycleTestsBase
{
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
        => _runner.RunReminderService_StartupReadiness(TestContext.Current.CancellationToken);

    [Fact]
    public Task ReminderService_RegistrationHasSingleOwner()
        => _runner.RunReminderService_RegistrationHasSingleOwner(TestContext.Current.CancellationToken);

    [Fact]
    public Task ReminderService_UpdateDoesNotRestartLocalOwner()
        => _runner.RunReminderService_UpdateDoesNotRestartLocalOwner(TestContext.Current.CancellationToken);

    [Fact]
    public Task ReminderService_RemovalReachesQuiescence()
        => _runner.RunReminderService_RemovalReachesQuiescence(TestContext.Current.CancellationToken);

    [Fact]
    public Task ReminderService_ExactDueRecovery()
        => _runner.RunReminderService_ExactDueRecovery(TestContext.Current.CancellationToken);

    [Fact]
    public Task ReminderService_StaleOwnerRegistrationReconciles()
        => _runner.RunReminderService_StaleOwnerRegistrationReconciles(TestContext.Current.CancellationToken);

    [Fact]
    public Task ReminderService_OneSiloJoinLeaveTransfersOwnership()
        => _runner.RunReminderService_OneSiloJoinLeaveTransfersOwnership(TestContext.Current.CancellationToken);

    [Fact]
    public Task ReminderService_CleanupIsIsolated()
        => _runner.RunReminderService_CleanupIsIsolated(TestContext.Current.CancellationToken);

    private sealed class ProviderRunner(IReminderServiceLifecycleHarness harness, string providerName)
        : ReminderServiceLifecycleTestRunner(harness, providerName);
}
