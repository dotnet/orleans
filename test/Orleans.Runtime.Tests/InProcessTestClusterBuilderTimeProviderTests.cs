using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
public class InProcessTestClusterBuilderTimeProviderTests
{
    private static readonly TimeSpan ReminderDueTime = TimeSpan.FromHours(1);
    private static readonly TimeSpan ReminderPeriod = TimeSpan.FromHours(12);
    private static readonly TimeSpan DueTimeBoundary = TimeSpan.FromMinutes(1);

    [Fact, TestCategory("BVT")]
    public async Task ConfigureHost_CanRegisterTimeProvider_ForClientAndSiloServices()
    {
        var fakeTimeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-21T12:00:00+00:00"));
        var builder = new InProcessTestClusterBuilder();
        builder.ConfigureHost(hostBuilder => hostBuilder.Services.AddSingleton<TimeProvider>(fakeTimeProvider));

        await using var cluster = builder.Build();
        await cluster.DeployAsync();

        Assert.Same(fakeTimeProvider, cluster.Client.ServiceProvider.GetRequiredService<TimeProvider>());
        Assert.Same(fakeTimeProvider, cluster.GetSiloServiceProvider().GetRequiredService<TimeProvider>());
        Assert.Same(fakeTimeProvider, cluster.GetSiloServiceProvider().GetRequiredService<IGrainRuntime>().TimeProvider);
    }

    [Fact, TestCategory("BVT")]
    public async Task ConfigureHost_CanControlReminderDueTimeUsingFakeTimeProvider()
    {
        using var observer = ReminderDiagnosticObserver.Create();
        var fakeTimeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-21T12:00:00+00:00"));
        var builder = CreateBuilder(fakeTimeProvider);

        await using var cluster = builder.Build();
        await cluster.DeployAsync();

        var grain = cluster.Client.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        const string reminderName = nameof(ConfigureHost_CanControlReminderDueTimeUsingFakeTimeProvider);

        await grain.StartReminder(reminderName, ReminderDueTime, ReminderPeriod);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        fakeTimeProvider.Advance(ReminderDueTime - DueTimeBoundary);
        await WaitForSingleLocalReminderScheduleAsync(observer, grain, reminderName, cancellation.Token);
        await AssertReminderTickCountAsync(grain, reminderName, expectedCount: 0);

        fakeTimeProvider.Advance(DueTimeBoundary);
        await observer.WaitForTickCountAsync(grain, expectedCount: 1, cancellation.Token, reminderName);
        await observer.WaitForReminderQuiescenceAsync(grain, reminderName, cancellation.Token);
        await AssertReminderTickCountAsync(grain, reminderName, expectedCount: 1);
    }

    [Fact, TestCategory("BVT")]
    public async Task ConfigureHost_CanAdvanceFakeTimeToTriggerSubsequentReminderTicks()
    {
        using var observer = ReminderDiagnosticObserver.Create();
        var fakeTimeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-21T12:00:00+00:00"));
        var builder = CreateBuilder(fakeTimeProvider);

        await using var cluster = builder.Build();
        await cluster.DeployAsync();

        var grain = cluster.Client.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        const string reminderName = nameof(ConfigureHost_CanAdvanceFakeTimeToTriggerSubsequentReminderTicks);

        await grain.StartReminder(reminderName, ReminderDueTime, ReminderPeriod);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        fakeTimeProvider.Advance(ReminderDueTime - DueTimeBoundary);
        await WaitForSingleLocalReminderScheduleAsync(observer, grain, reminderName, cancellation.Token);
        fakeTimeProvider.Advance(DueTimeBoundary);
        await observer.WaitForTickCountAsync(grain, expectedCount: 1, cancellation.Token, reminderName);
        await observer.WaitForReminderQuiescenceAsync(grain, reminderName, cancellation.Token);
        await AssertReminderTickCountAsync(grain, reminderName, expectedCount: 1);

        fakeTimeProvider.Advance(ReminderPeriod - DueTimeBoundary);
        await WaitForSingleLocalReminderScheduleAsync(observer, grain, reminderName, cancellation.Token);
        fakeTimeProvider.Advance(DueTimeBoundary);
        await observer.WaitForTickCountAsync(grain, expectedCount: 2, cancellation.Token, reminderName);
        await observer.WaitForReminderQuiescenceAsync(grain, reminderName, cancellation.Token);
        await AssertReminderTickCountAsync(grain, reminderName, expectedCount: 2);

        fakeTimeProvider.Advance(ReminderPeriod - DueTimeBoundary);
        await WaitForSingleLocalReminderScheduleAsync(observer, grain, reminderName, cancellation.Token);
        fakeTimeProvider.Advance(DueTimeBoundary);
        await observer.WaitForTickCountAsync(grain, expectedCount: 3, cancellation.Token, reminderName);
        await observer.WaitForReminderQuiescenceAsync(grain, reminderName, cancellation.Token);
        await AssertReminderTickCountAsync(grain, reminderName, expectedCount: 3);
    }

    private static InProcessTestClusterBuilder CreateBuilder(FakeTimeProvider fakeTimeProvider)
    {
        var builder = new InProcessTestClusterBuilder();
        builder.ConfigureHost(hostBuilder => hostBuilder.Services.AddSingleton<TimeProvider>(fakeTimeProvider));
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.Configure<ReminderOptions>(options => options.MinimumReminderPeriod = TimeSpan.FromMinutes(1));
            siloBuilder.UseInMemoryReminderService();
        });

        return builder;
    }

    private static async Task WaitForSingleLocalReminderScheduleAsync(
        ReminderDiagnosticObserver observer,
        IReminderTestGrain2 grain,
        string reminderName,
        CancellationToken cancellationToken)
    {
        await observer.WaitForLocalReminderScheduleAsync(grain, reminderName, cancellationToken);
        Assert.Equal(1, observer.GetActiveReminderCount(grain.GetGrainId(), reminderName));
    }

    private static async Task AssertReminderTickCountAsync(IReminderTestGrain2 grain, string reminderName, int expectedCount)
    {
        var states = await grain.GetReminderStates();
        Assert.True(states.TryGetValue(reminderName, out var reminderState));
        Assert.Equal(expectedCount, reminderState.Fired.Count);
    }

}
