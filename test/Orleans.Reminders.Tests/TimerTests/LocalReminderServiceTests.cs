using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    public void CalculateFollowingTickTime_ReturnsNextScheduledOccurrence()
    {
        var period = TimeSpan.FromDays(60);
        var startAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = CreateReminderEntry(startAt, period);

        var nextTick = LocalReminderService.CalculateFollowingTickTime(entry, startAt, startAt);

        Assert.Equal(startAt + period, nextTick);
    }

    [Fact, TestCategory("BVT")]
    public void CalculateFollowingTickTime_SkipsMissedOccurrencesWithoutDrifting()
    {
        var period = TimeSpan.FromMinutes(10);
        var startAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = CreateReminderEntry(startAt, period);

        var nextTick = LocalReminderService.CalculateFollowingTickTime(
            entry,
            startAt,
            startAt + TimeSpan.FromMinutes(25));

        Assert.Equal(startAt + TimeSpan.FromMinutes(30), nextTick);
    }

    [Theory, TestCategory("BVT")]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void IsReminderWithinLoadingWindow_UsesInclusiveBoundary(int millisecondsInsideWindow, bool expected)
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var loadingWindow = TimeSpan.FromMinutes(10);
        var entry = CreateReminderEntry(now + loadingWindow + TimeSpan.FromMilliseconds(-millisecondsInsideWindow), TimeSpan.FromHours(1));

        var result = LocalReminderService.IsReminderWithinLoadingWindow(entry, now, loadingWindow);

        Assert.Equal(expected, result);
    }

    [Fact, TestCategory("BVT")]
    public void ReminderOptions_DefaultLoadingWindowIsTwiceDefaultRefreshPeriod()
    {
        var options = new ReminderOptions();

        Assert.Equal(2 * options.RefreshReminderListPeriod, options.ReminderLoadingWindow);
    }

    [Fact, TestCategory("BVT")]
    public void ReminderOptionsValidator_RejectsNonPositiveLoadingWindow()
    {
        var options = new ReminderOptions { ReminderLoadingWindow = TimeSpan.Zero };

        Assert.Throws<OrleansConfigurationException>(() => Validate(options));
    }

    [Theory, TestCategory("BVT")]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void ReminderOptionsValidator_RejectsLoadingWindowShorterThanRefreshPeriod(int differenceInSeconds, bool throws)
    {
        var refreshPeriod = TimeSpan.FromMinutes(5);
        var options = new ReminderOptions
        {
            RefreshReminderListPeriod = refreshPeriod,
            ReminderLoadingWindow = refreshPeriod + TimeSpan.FromSeconds(differenceInSeconds),
        };

        var exception = Record.Exception(() => Validate(options));

        Assert.Equal(throws, exception is OrleansConfigurationException);
    }

    private static void Validate(ReminderOptions options)
    {
        var validator = new ReminderOptionsValidator(
            NullLogger<ReminderOptionsValidator>.Instance,
            Options.Create(options));
        validator.ValidateConfiguration();
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
