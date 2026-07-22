using Microsoft.Extensions.DependencyInjection;
using Orleans.AdvancedReminders;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests;

public sealed class AdvancedReminderInMemoryTests(AdvancedReminderInMemoryTests.Fixture fixture)
    : IClassFixture<AdvancedReminderInMemoryTests.Fixture>
{
    [Fact, TestCategory("BVT"), TestCategory("Reminders")]
    public async Task InMemoryProvider_ProxyImplementsCrudRangesAndCompareExchange()
    {
        var grain = fixture.Cluster.GrainFactory.GetGrain<IAdvancedReminderTestGrain>(Random.Shared.NextInt64());
        const string reminderName = "in-memory-proxy-cas";
        await grain.ClearRawTable();

        var firstETag = await grain.UpsertRaw(reminderName, string.Empty);
        Assert.False(string.IsNullOrWhiteSpace(firstETag));
        Assert.Equal(firstETag, await grain.ReadRawETag(reminderName));
        Assert.Equal(1, await grain.ReadRawGrainCount());
        Assert.Equal(1, await grain.ReadRawContainingRangeCount());

        await Assert.ThrowsAsync<Orleans.AdvancedReminders.Runtime.ReminderException>(
            () => grain.UpsertRaw(reminderName, "stale-etag"));
        Assert.Equal(firstETag, await grain.ReadRawETag(reminderName));
        Assert.False(await grain.RemoveRaw(reminderName, "stale-etag"));

        var secondETag = await grain.UpsertRaw(reminderName, firstETag);
        Assert.NotEqual(firstETag, secondETag);
        Assert.True(await grain.RemoveRaw(reminderName, secondETag));
        Assert.Null(await grain.ReadRawETag(reminderName));

        await grain.UpsertRaw(reminderName, string.Empty);
        await grain.ClearRawTable();
        Assert.Equal(0, await grain.ReadRawGrainCount());
    }

    [Fact, TestCategory("BVT"), TestCategory("Reminders")]
    public async Task InMemoryProvider_RoundTripsAndDeliversReminder()
    {
        var grain = fixture.Cluster.GrainFactory.GetGrain<IAdvancedReminderTestGrain>(Random.Shared.NextInt64());
        const string reminderName = "in-memory-round-trip";

        await grain.Register(reminderName, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100));
        Assert.True(await grain.Exists(reminderName));

        await WaitUntilAsync(async () => await grain.GetTickCount() > 0, TimeSpan.FromSeconds(10));

        await grain.Unregister(reminderName);
        Assert.False(await grain.Exists(reminderName));

        var tickCount = await grain.GetTickCount();
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Equal(tickCount, await grain.GetTickCount());
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellation.Token);
        }
    }

    public sealed class Fixture : IAsyncLifetime
    {
        public TestCluster Cluster { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            var builder = new TestClusterBuilder(1);
            TestDefaultConfiguration.ConfigureTestCluster(builder);
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            Cluster = builder.Build();
            await Cluster.DeployAsync();
        }

        public async Task DisposeAsync()
        {
            try
            {
                await Cluster.StopAllSilosAsync();
            }
            finally
            {
                await Cluster.DisposeAsync();
            }
        }

        private sealed class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder siloBuilder)
            {
                siloBuilder.UseInMemoryAdvancedReminderService();
                siloBuilder.Services.Configure<Orleans.AdvancedReminders.ReminderOptions>(options =>
                    options.MinimumReminderPeriod = TimeSpan.FromMilliseconds(10));
            }
        }
    }
}
