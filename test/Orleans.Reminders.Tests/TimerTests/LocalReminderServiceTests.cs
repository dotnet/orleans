using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.TimerTests;

public class LocalReminderServiceTests
{
    [Fact, TestCategory("BVT")]
    public void CalculateInitialDueTime_ReturnsMinimumDueTime_WhenNextTickIsDueNow()
    {
        var period = TimeSpan.FromSeconds(12);
        var startAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = CreateReminderEntry(startAt, period);
        var now = startAt + period;

        var dueTime = LocalReminderService.CalculateInitialDueTime(entry, now);

        Assert.Equal(TimeSpan.FromMilliseconds(1), dueTime);
    }

    [Fact, TestCategory("BVT")]
    public void CalculateInitialDueTime_ReturnsMinimumDueTime_WhenNextTickIsWithinMinimumDueTime()
    {
        var period = TimeSpan.FromSeconds(12);
        var startAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = CreateReminderEntry(startAt, period);
        var now = startAt + period - TimeSpan.FromTicks(1);

        var dueTime = LocalReminderService.CalculateInitialDueTime(entry, now);

        Assert.Equal(TimeSpan.FromMilliseconds(1), dueTime);
    }

    [Fact, TestCategory("BVT")]
    public void CalculateInitialDueTime_ReturnsRemainingDueTime_WhenNextTickIsAtLeastMinimumDueTime()
    {
        var period = TimeSpan.FromSeconds(12);
        var startAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = CreateReminderEntry(startAt, period);
        var now = startAt + period - TimeSpan.FromMilliseconds(10);

        var dueTime = LocalReminderService.CalculateInitialDueTime(entry, now);

        Assert.Equal(TimeSpan.FromMilliseconds(10), dueTime);
    }

    [Fact, TestCategory("BVT")]
    public void CalculateInitialDueTime_ReturnsRemainingPeriod_WhenNextTickIsInFuture()
    {
        var period = TimeSpan.FromSeconds(12);
        var startAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = CreateReminderEntry(startAt, period);
        var now = startAt + TimeSpan.FromSeconds(3);

        var dueTime = LocalReminderService.CalculateInitialDueTime(entry, now);

        Assert.Equal(TimeSpan.FromSeconds(9), dueTime);
    }

    [Fact, TestCategory("BVT")]
    public void CalculateNextDueTime_ClampsOverflow()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var dueTime = LocalReminderService.CalculateNextDueTime(now, TimeSpan.MaxValue);

        Assert.Equal(DateTime.MaxValue, dueTime);
    }

    private static ReminderEntry CreateReminderEntry(DateTime startAt, TimeSpan period)
    {
        return new ReminderEntry
        {
            GrainId = GrainId.Create("test", "grain"),
            ReminderName = "reminder",
            StartAt = startAt,
            Period = period,
            ETag = "etag",
        };
    }
}

public class LocalReminderServiceCompatibilityTests : IClassFixture<LocalReminderServiceCompatibilityTests.Fixture>
{
    private readonly Fixture fixture;

    public LocalReminderServiceCompatibilityTests(Fixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact, TestCategory("BVT")]
    public async Task InitialRead_TreatsNullTableResultAsNoWork()
    {
        var silo = Assert.Single(fixture.HostedCluster.Silos);
        using var cancellation = new CancellationTokenSource(TestConstants.InitTimeout);

        var started = await fixture.ReminderObserver.WaitForReminderServiceStartedAsync(cancellation.Token, silo.SiloAddress);

        Assert.Equal(silo.SiloAddress, started.SiloAddress);
        var reminderTable = silo.ServiceProvider.GetRequiredService<NullReturningReminderTable>();
        Assert.True(reminderTable.RangeReadCount > 0);
    }

    public sealed class Fixture : BaseInProcessTestClusterFixture
    {
        public ReminderDiagnosticObserver ReminderObserver { get; } = ReminderDiagnosticObserver.Create();

        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            builder.ConfigureSilo((_, siloBuilder) =>
            {
                siloBuilder.AddReminders();
                siloBuilder.ConfigureServices(services =>
                {
                    services.AddSingleton<NullReturningReminderTable>();
                    services.AddSingleton<IReminderTable>(
                        static provider => provider.GetRequiredService<NullReturningReminderTable>());
                });
            });
        }

        public override async Task DisposeAsync()
        {
            try
            {
                await base.DisposeAsync();
            }
            finally
            {
                ReminderObserver.Dispose();
            }
        }
    }

    private sealed class NullReturningReminderTable : IReminderTable
    {
        private int rangeReadCount;

        public int RangeReadCount => Volatile.Read(ref rangeReadCount);

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            Interlocked.Increment(ref rangeReadCount);
            // Simulate a provider binary compiled before the return value was annotated as non-null.
            return Task.FromResult<ReminderTableData>(null!);
        }

        public Task<ReminderTableData> ReadRows(GrainId grainId) => throw new NotSupportedException();

        public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName) => throw new NotSupportedException();

        public Task<string?> UpsertRow(ReminderEntry entry) => throw new NotSupportedException();

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) => throw new NotSupportedException();

        public Task TestOnlyClearTable() => throw new NotSupportedException();
    }
}
