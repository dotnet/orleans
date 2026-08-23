using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void CalculateFollowingTickTime_ReturnsNextScheduledOccurrence()
    {
        var period = TimeSpan.FromDays(60);
        var startAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = CreateReminderEntry(startAt, period);

        var nextTick = LocalReminderService.CalculateFollowingTickTime(entry, startAt, startAt);

        Assert.Equal(startAt + period, nextTick);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
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

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Theory, TestCategory("BVT")]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public void CalculateNextTickTime_PreservesCadenceAtOccurrenceBoundary(int offsetTicks, int expectedAdditionalPeriods)
    {
        var period = TimeSpan.FromMinutes(10);
        var startAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var boundary = startAt + (2 * period);
        var entry = CreateReminderEntry(startAt, period);

        var nextTick = LocalReminderService.CalculateNextTickTime(entry, boundary.AddTicks(offsetTicks));

        Assert.Equal(boundary + (expectedAdditionalPeriods * period), nextTick);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void CalculateNextTickTime_TreatsPersistedUnspecifiedTimestampAsUtc()
    {
        var persistedStartAt = new DateTime(2026, 1, 1, 0, 10, 0, DateTimeKind.Unspecified);
        var entry = CreateReminderEntry(persistedStartAt, TimeSpan.FromMinutes(10));
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var nextTick = LocalReminderService.CalculateNextTickTime(entry, now);

        Assert.Equal(DateTimeKind.Utc, nextTick.Kind);
        Assert.Equal(persistedStartAt.Ticks, nextTick.Ticks);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
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

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Theory, TestCategory("BVT")]
    [InlineData(true, 0, true, true)]
    [InlineData(false, 0, true, false)]
    [InlineData(true, 0, false, false)]
    [InlineData(true, 1, true, false)]
    public void ShouldRetainFiredOccurrenceTombstone_RequiresMatchingFiredTickAndSchedule(
        bool hasFiredTick,
        int nextTickOffsetTicks,
        bool sameETag,
        bool expected)
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var localEntry = CreateReminderEntry(now, TimeSpan.FromMinutes(10));
        var tableEntry = CreateReminderEntry(now, TimeSpan.FromMinutes(10));
        if (!sameETag)
        {
            tableEntry.ETag = "updated-etag";
        }

        var result = LocalReminderService.ShouldRetainFiredOccurrenceTombstone(
            localEntry,
            tableEntry,
            hasFiredTick ? now : null,
            now.AddTicks(nextTickOffsetTicks));

        Assert.Equal(expected, result);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void ReminderOptions_DefaultLoadingWindowIsTwiceDefaultRefreshPeriod()
    {
        var options = new ReminderOptions();

        Assert.Equal(2 * options.RefreshReminderListPeriod, options.ReminderLoadingWindow);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void ReminderOptionsValidator_RejectsNonPositiveLoadingWindow()
    {
        var options = new ReminderOptions { ReminderLoadingWindow = TimeSpan.Zero };

        Assert.Throws<OrleansConfigurationException>(() => Validate(options));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
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

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void AddReminders_RegistersReminderOptionsValidatorOnce_AndValidatesInvalidConfigurationThroughDI()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.Configure<ReminderOptions>(options =>
        {
            options.RefreshReminderListPeriod = TimeSpan.FromMinutes(5);
            options.ReminderLoadingWindow = TimeSpan.FromMinutes(1);
        });

        // AddReminders must be idempotent: calling it twice must not register the validator twice.
        services.AddReminders();
        services.AddReminders();

        var validatorRegistrations = services.Where(descriptor => descriptor.ServiceType == typeof(IConfigurationValidator)).ToList();
        Assert.Single(validatorRegistrations);

        using var serviceProvider = services.BuildServiceProvider();
        var validators = serviceProvider.GetServices<IConfigurationValidator>().ToList();
        Assert.Single(validators);
        Assert.IsType<ReminderOptionsValidator>(validators[0]);

        // The invalid configuration must be rejected via the resolved IConfigurationValidator, not by
        // constructing ReminderOptionsValidator directly.
        var exception = Assert.Throws<OrleansConfigurationException>(() =>
        {
            foreach (IConfigurationValidator resolvedValidator in validators)
            {
                resolvedValidator.ValidateConfiguration();
            }
        });

        Assert.Contains(nameof(ReminderOptions.ReminderLoadingWindow), exception.Message);
        Assert.Contains(nameof(ReminderOptions.RefreshReminderListPeriod), exception.Message);
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

    [TestSuite("BVT")]
    [TestProvider("None")]
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

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public async Task RangeChangeBarrier_WaitsForReconciliation()
    {
        var silo = Assert.Single(fixture.HostedCluster.Silos);
        using var cancellation = new CancellationTokenSource(TestConstants.InitTimeout);
        _ = await fixture.ReminderObserver.WaitForReminderServiceStartedAsync(cancellation.Token, silo.SiloAddress);

        var reminderTable = silo.ServiceProvider.GetRequiredService<NullReturningReminderTable>();
        var reminderService = silo.ServiceProvider.GetRequiredService<LocalReminderService>();
        var oldRange = RangeFactory.CreateRange(0, uint.MaxValue / 2);
        var intermediateRange = RangeFactory.CreateRange(0, uint.MaxValue / 3);
        var newRange = RangeFactory.CreateRange(0, uint.MaxValue / 4);
        var firstReadGate = reminderTable.BlockNextRangeRead();
        RangeReadGate? secondReadGate = null;
        try
        {
            var firstRangeChangeTask = reminderService.TestOnlyChangeRange(oldRange, intermediateRange, increased: false);
            await firstReadGate.WaitUntilBlockedAsync(cancellation.Token);

            secondReadGate = reminderTable.BlockNextRangeRead();
            var secondRangeChangeTask = reminderService.TestOnlyChangeRange(intermediateRange, newRange, increased: false);
            await secondReadGate.WaitUntilBlockedAsync(cancellation.Token);

            var reconciliationTask = reminderService.TestOnlyWaitForRangeChangeReconciliation(cancellation.Token);
            Assert.False(reconciliationTask.IsCompleted);

            secondReadGate.Release();
            await secondRangeChangeTask.WaitAsync(cancellation.Token);
            Assert.False(reconciliationTask.IsCompleted);

            firstReadGate.Release();
            await Task.WhenAll(firstRangeChangeTask, reconciliationTask).WaitAsync(cancellation.Token);
        }
        finally
        {
            firstReadGate.Release();
            secondReadGate?.Release();
        }
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

        public override async ValueTask DisposeAsync()
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
        private RangeReadGate? nextRangeRead;

        public int RangeReadCount => Volatile.Read(ref rangeReadCount);

        public RangeReadGate BlockNextRangeRead()
        {
            var gate = new RangeReadGate();
            if (Interlocked.CompareExchange(ref nextRangeRead, gate, null) is not null)
            {
                throw new InvalidOperationException("A range read is already blocked.");
            }

            return gate;
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            Interlocked.Increment(ref rangeReadCount);
            if (Interlocked.Exchange(ref nextRangeRead, null) is { } gate)
            {
                gate.MarkBlocked();
                await gate.WaitForReleaseAsync();
            }

            // Simulate a provider binary compiled before the return value was annotated as non-null.
            return null!;
        }

        public Task<ReminderTableData> ReadRows(GrainId grainId) => throw new NotSupportedException();

        public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName) => throw new NotSupportedException();

        public Task<string?> UpsertRow(ReminderEntry entry) => throw new NotSupportedException();

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) => throw new NotSupportedException();

        public Task TestOnlyClearTable() => throw new NotSupportedException();
    }

    private sealed class RangeReadGate
    {
        private readonly TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void MarkBlocked() => blocked.TrySetResult();

        public Task WaitUntilBlockedAsync(CancellationToken cancellationToken) => blocked.Task.WaitAsync(cancellationToken);

        public Task WaitForReleaseAsync() => release.Task;

        public void Release() => release.TrySetResult();
    }
}
