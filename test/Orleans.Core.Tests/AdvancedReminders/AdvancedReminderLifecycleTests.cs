#nullable enable
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Xunit;
using AdvancedReminderOptions = Orleans.AdvancedReminders.ReminderOptions;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class AdvancedReminderLifecycleTests : AdvancedReminderServiceTestBase
{
    [Fact]
    public async Task LifecycleStart_WhenInitializationExceedsConfiguredTimeout_ThrowsTimeoutException()
    {
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.StartAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.Delay(Timeout.InfiniteTimeSpan, callInfo.Arg<CancellationToken>()));
        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { InitializationTimeout = TimeSpan.FromMilliseconds(25) });
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        service.Participate(lifecycle);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => lifecycle.OnStart(TestContext.Current.CancellationToken));

        Assert.Contains("00:00:00.025", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LifecycleStart_WhenProviderIgnoresCancellation_StillEnforcesConfiguredTimeout()
    {
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.StartAsync(Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { InitializationTimeout = TimeSpan.FromMilliseconds(25) });
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        service.Participate(lifecycle);

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => lifecycle.OnStart(TestContext.Current.CancellationToken))
            .WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Contains("00:00:00.025", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lifecycle_RecoveryHeartbeatKeepsSingletonRecoverable()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero));
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        var recovery = Substitute.For<IAdvancedReminderRecoveryGrain>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderRecoveryGrain>(0, null).Returns(recovery);
        var service = CreateService(reminderTable, grainFactory: grainFactory, timeProvider: timeProvider);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        service.Participate(lifecycle);

        await lifecycle.OnStart(TestContext.Current.CancellationToken);
        await recovery.Received(1).StartAsync(force: false, Arg.Any<CancellationToken>());

        timeProvider.Advance(AdvancedReminderService.RecoveryHeartbeatPeriod);
        for (var attempt = 0; attempt < 100
            && recovery.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IAdvancedReminderRecoveryGrain.StartAsync)) < 2;
            attempt++)
        {
            await Task.Yield();
        }

        await recovery.Received(2).StartAsync(force: false, Arg.Any<CancellationToken>());
        await lifecycle.OnStop(TestContext.Current.CancellationToken);
    }
}
