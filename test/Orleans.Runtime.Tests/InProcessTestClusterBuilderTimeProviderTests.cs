using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester;

public class InProcessTestClusterBuilderTimeProviderTests
{
    private static readonly TimeSpan ReminderPeriod = TimeSpan.FromMilliseconds(100);

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
        var fakeTimeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-21T12:00:00+00:00"));
        var builder = CreateBuilder(fakeTimeProvider);

        await using var cluster = builder.Build();
        await cluster.DeployAsync();

        var grain = cluster.Client.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        const string reminderName = nameof(ConfigureHost_CanControlReminderDueTimeUsingFakeTimeProvider);

        await grain.StartReminder(reminderName, ReminderPeriod, validate: true);

        fakeTimeProvider.Advance(TimeSpan.FromMilliseconds(1800));
        await Task.Delay(100);
        await AssertReminderTickCountAsync(grain, reminderName, expectedCount: 0);

        fakeTimeProvider.Advance(TimeSpan.FromMilliseconds(200));
        await WaitForReminderTickCountAsync(grain, reminderName, expectedCount: 1);
    }

    [Fact, TestCategory("BVT")]
    public async Task ConfigureHost_CanAdvanceFakeTimeToTriggerSubsequentReminderTicks()
    {
        var fakeTimeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-21T12:00:00+00:00"));
        var builder = CreateBuilder(fakeTimeProvider);

        await using var cluster = builder.Build();
        await cluster.DeployAsync();

        var grain = cluster.Client.GetGrain<IReminderTestGrain2>(Guid.NewGuid());
        const string reminderName = nameof(ConfigureHost_CanAdvanceFakeTimeToTriggerSubsequentReminderTicks);

        await grain.StartReminder(reminderName, ReminderPeriod, validate: true);

        fakeTimeProvider.Advance(TimeSpan.FromSeconds(2));
        await WaitForReminderTickCountAsync(grain, reminderName, expectedCount: 1);

        fakeTimeProvider.Advance(ReminderPeriod);
        await WaitForReminderTickCountAsync(grain, reminderName, expectedCount: 2);

        fakeTimeProvider.Advance(ReminderPeriod);
        await WaitForReminderTickCountAsync(grain, reminderName, expectedCount: 3);
    }

    private static InProcessTestClusterBuilder CreateBuilder(FakeTimeProvider fakeTimeProvider)
    {
        var builder = new InProcessTestClusterBuilder();
        builder.ConfigureHost(hostBuilder => hostBuilder.Services.AddSingleton<TimeProvider>(fakeTimeProvider));
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.Configure<ReminderOptions>(options => options.MinimumReminderPeriod = ReminderPeriod);
            siloBuilder.UseInMemoryReminderService();
        });

        return builder;
    }

    private static async Task AssertReminderTickCountAsync(IReminderTestGrain2 grain, string reminderName, int expectedCount)
    {
        var states = await grain.GetReminderStates();
        Assert.True(states.TryGetValue(reminderName, out var reminderState));
        Assert.Equal(expectedCount, reminderState.Fired.Count);
    }

    private static async Task WaitForReminderTickCountAsync(IReminderTestGrain2 grain, string reminderName, int expectedCount)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        while (!cancellation.IsCancellationRequested)
        {
            var states = await grain.GetReminderStates();
            if (states.TryGetValue(reminderName, out var reminderState) && reminderState.Fired.Count >= expectedCount)
            {
                return;
            }

            await Task.Delay(50, cancellation.Token);
        }

        Assert.Fail($"Reminder '{reminderName}' did not reach {expectedCount} ticks.");
    }
}
