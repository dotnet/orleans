using Orleans.Runtime;
using TestExtensions;
using UnitTests.MembershipTests;
using Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Internal;
using Orleans.Configuration;
using Orleans.TestingHost.Utils;
using Orleans.Reminders.TestKit;

namespace UnitTests.RemindersTest
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public abstract class ReminderTableTestsBase : IAsyncLifetime, IClassFixture<ConnectionStringFixture>
    {
        protected readonly TestEnvironmentFixture ClusterFixture;
        private readonly ILogger logger;

        private readonly IReminderTable remindersTable;
        protected ILoggerFactory loggerFactory;
        protected IOptions<ClusterOptions> clusterOptions;

        protected ConnectionStringFixture connectionStringFixture;

        protected const string testDatabaseName = "OrleansReminderTest";//for relational storage

        protected ReminderTableTestsBase(ConnectionStringFixture fixture, TestEnvironmentFixture clusterFixture, LoggerFilterOptions filters)
        {
            this.connectionStringFixture = fixture;
            fixture.InitializeConnectionStringAccessor(GetConnectionString);
            loggerFactory = TestingUtils.CreateDefaultLoggerFactory($"{this.GetType()}.log", filters);
            this.ClusterFixture = clusterFixture;
            logger = loggerFactory.CreateLogger<ReminderTableTestsBase>();
            var serviceId = Guid.NewGuid().ToString() + "/foo";
            var clusterId = "test-" + serviceId + "/foo2";

            logger.LogInformation("ClusterId={ClusterId}", clusterId);
            this.clusterOptions = Options.Create(new ClusterOptions { ClusterId = clusterId, ServiceId = serviceId });

            this.remindersTable = this.CreateRemindersTable();
        }

        public virtual async ValueTask InitializeAsync()
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await this.remindersTable.StartAsync(cancellation.Token);
        }

        public virtual async ValueTask DisposeAsync()
        {
            if (remindersTable != null && SiloInstanceTableTestConstants.DeleteEntriesAfterTest)
            {
                await remindersTable.TestOnlyClearTable();
            }
        }

        protected abstract IReminderTable CreateRemindersTable();
        protected abstract Task<string> GetConnectionString();
        protected IReminderTable RemindersTable => remindersTable;

        private ReminderTableTestRunner CreateConformanceRunner()
            => new ProviderReminderTableTestRunner(
                remindersTable,
                GetType().Name);

        protected virtual string? GetAdoInvariant()
        {
            return null;
        }

        protected async Task RemindersParallelUpsert()
        {
            await CreateConformanceRunner().ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated();
        }

        protected async Task ReminderSimple()
        {
            var runner = CreateConformanceRunner();
            await runner.ReminderTable_UpsertRow_PersistsScheduleForPointRead();
            await runner.ReminderTable_Identity_WithSpecialCharacters_RoundTrips();
            await runner.ReminderTable_RemoveRow_WithUnknownReminderName_ReturnsFalse();
            await runner.ReminderTable_RemoveRow_Repeated_ReturnsFalseAfterFirstSuccess();
        }

        protected async Task RemindersRange(int iterations = 1000)
        {
            await RunRemindersRange(CreateConformanceRunner(), iterations);
        }

        internal static async Task RunRemindersRange(ReminderTableTestRunner runner, int iterations)
        {
            ArgumentNullException.ThrowIfNull(runner);

            await runner.ReminderTable_ReadRows_FullRange_ReturnsExactRequestedCardinality(iterations);
            await runner.ReminderTable_ReadRows_FullRange_ReturnsAllReminders();
        }

        [Fact]
        public Task ReminderTable_StartAsync_IsIdempotent() => CreateConformanceRunner().ReminderTable_StartAsync_IsIdempotent();

        [Fact]
        public Task ReminderTable_StopAsync_ThenRestart_ResumesService()
            => CreateConformanceRunner().ReminderTable_StopAsync_ThenRestart_ResumesService();

        [Fact]
        public Task ReminderTable_UpsertRow_ReturnsNewNonEmptyETag() => CreateConformanceRunner().ReminderTable_UpsertRow_ReturnsNewNonEmptyETag();

        [Fact]
        public Task ReminderTable_UpsertRow_PersistsScheduleForPointRead() => CreateConformanceRunner().ReminderTable_UpsertRow_PersistsScheduleForPointRead();

        [Fact]
        public Task ReminderTable_ReadRow_MissingReminder_ReturnsNull() => CreateConformanceRunner().ReminderTable_ReadRow_MissingReminder_ReturnsNull();

        [Fact]
        public Task ReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders() => CreateConformanceRunner().ReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders();

        [Fact]
        public Task ReminderTable_ReadRows_ForUnknownGrain_ReturnsEmpty() => CreateConformanceRunner().ReminderTable_ReadRows_ForUnknownGrain_ReturnsEmpty();

        [Fact]
        public Task ReminderTable_Identity_IsGrainIdAndReminderName() => CreateConformanceRunner().ReminderTable_Identity_IsGrainIdAndReminderName();

        [Fact]
        public Task ReminderTable_Identity_WithSpecialCharacters_RoundTrips() => CreateConformanceRunner().ReminderTable_Identity_WithSpecialCharacters_RoundTrips();

        [Fact]
        public Task ReminderTable_UpsertRow_ReplacesETagOnEachWrite()
            => CreateConformanceRunner().ReminderTable_UpsertRow_ReplacesETagOnEachWrite();

        [Fact]
        public Task ReminderTable_RemoveRow_WithCurrentETag_RemovesRow() => CreateConformanceRunner().ReminderTable_RemoveRow_WithCurrentETag_RemovesRow();

        [Fact]
        public Task ReminderTable_RemoveRow_WithStaleETag_FailsAndRetainsRow()
            => CreateConformanceRunner().ReminderTable_RemoveRow_WithStaleETag_FailsAndRetainsRow();

        [Fact]
        public Task ReminderTable_RemoveRow_WithUnknownReminderName_ReturnsFalse() => CreateConformanceRunner().ReminderTable_RemoveRow_WithUnknownReminderName_ReturnsFalse();

        [Fact]
        public Task ReminderTable_RemoveRow_Repeated_ReturnsFalseAfterFirstSuccess() => CreateConformanceRunner().ReminderTable_RemoveRow_Repeated_ReturnsFalseAfterFirstSuccess();

        [Fact]
        public Task ReminderTable_UpsertRow_UpdatesStartAtAndPeriod() => CreateConformanceRunner().ReminderTable_UpsertRow_UpdatesStartAtAndPeriod();

        [Fact]
        public Task ReminderTable_UpsertRow_MovesReminderBetweenLoadingWindows() => CreateConformanceRunner().ReminderTable_UpsertRow_MovesReminderBetweenLoadingWindows();

        [Fact]
        public Task ReminderTable_ReadRows_FullRange_ReturnsAllReminders() => CreateConformanceRunner().ReminderTable_ReadRows_FullRange_ReturnsAllReminders();

        [Fact]
        public Task ReminderTable_ReadRows_UnsignedBoundary_UsesUInt32Ordering()
            => CreateConformanceRunner().ReminderTable_ReadRows_UnsignedBoundary_UsesUInt32Ordering();

        [Fact]
        public Task ReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd()
            => CreateConformanceRunner().ReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd();

        [Fact]
        public Task ReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment()
            => CreateConformanceRunner().ReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment();

        [Fact]
        public Task ReminderTable_ReadRows_OutsideRange_DoesNotDeleteReminder() => CreateConformanceRunner().ReminderTable_ReadRows_OutsideRange_DoesNotDeleteReminder();

        [Fact]
        public Task ReminderTable_ReadRows_AfterRemoval_OmitsRemovedReminder() => CreateConformanceRunner().ReminderTable_ReadRows_AfterRemoval_OmitsRemovedReminder();

        [Fact]
        public Task ReminderTable_ReadRow_AfterRemoval_ReturnsNull() => CreateConformanceRunner().ReminderTable_ReadRow_AfterRemoval_ReturnsNull();

        [Fact]
        public Task ReminderTable_ConcurrentUpserts_ProduceDistinctETags()
            => CreateConformanceRunner().ReminderTable_ConcurrentUpserts_ProduceDistinctETags();

        [Fact]
        public Task ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated()
            => CreateConformanceRunner().ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated();

        [Fact]
        public Task ReminderTable_TestOnlyClearTable_RemovesAllReminders() => CreateConformanceRunner().ReminderTable_TestOnlyClearTable_RemovesAllReminders();

        [Fact, TestCategory("ModelBased")]
        public Task ReminderTable_ModelBasedGeneratedConformance()
            => new ReminderTableModelBasedTestRunner(remindersTable, GetType().Name).RunGeneratedConformanceTests();

        private sealed class ProviderReminderTableTestRunner(IReminderTable table, string providerName)
            : ReminderTableTestRunner(table, providerName);
    }
}
