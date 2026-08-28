using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Reminders.DynamoDB;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests;
using UnitTests.RemindersTest;
using UnitTests.TimerTests;
using Xunit;

namespace AWSUtils.Tests.RemindersTest
{
    public sealed class DynamoDBReminderServiceLifecycleFixture : BaseInProcessTestClusterFixture
    {
        private ReminderTestClock? _clock;

        public ReminderTestClock Clock
        {
            get
            {
                EnsurePreconditionsMet();
                return _clock ?? throw new InvalidOperationException("The reminder clock has not been configured.");
            }
        }

        protected override void CheckPreconditionsOrThrow()
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
            {
                throw Xunit.Sdk.SkipException.ForSkip("Unable to connect to AWS DynamoDB simulator");
            }
        }

        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            _clock = builder.AddReminderTestClock();
            builder.ConfigureSilo((_, siloBuilder) =>
                siloBuilder.UseDynamoDBReminderService(options =>
                    options.ParseConnectionString($"Service={AWSTestConstants.DynamoDbService}")));
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await base.DisposeAsync();
            }
            finally
            {
                _clock?.Dispose();
            }
        }
    }

    [TestCategory("Reminders"), TestCategory("AWS"), TestCategory("DynamoDb")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    [TestSuite("Functional")]
    [TestProvider("DynamoDB")]
    [TestArea("Reminders")]
    public sealed class DynamoDBReminderServiceLifecycleTests
        : ReminderServiceLifecycleTestsBase, IClassFixture<DynamoDBReminderServiceLifecycleFixture>
    {
        public DynamoDBReminderServiceLifecycleTests(DynamoDBReminderServiceLifecycleFixture fixture)
            : base(fixture.Clock, fixture.HostedCluster, "DynamoDB")
        {
            fixture.EnsurePreconditionsMet();
        }
    }

    /// <summary>
    /// Tests DynamoDB implementation of the Orleans reminders table for storing and retrieving grain reminders.
    /// </summary>
    [TestCategory("Reminders"), TestCategory("AWS"), TestCategory("DynamoDb")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    [TestSuite("Functional")]
    [TestProvider("DynamoDB")]
    [TestArea("Reminders")]
    public class DynamoDBRemindersTableTests : ReminderTableTestsBase, IClassFixture<DynamoDBStorageTestsFixture>
    {
        public DynamoDBRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, new LoggerFilterOptions())
        {
        }

        protected override IReminderTable CreateRemindersTable()
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
                throw Xunit.Sdk.SkipException.ForSkip("Unable to connect to AWS DynamoDB simulator");

            var options = new DynamoDBReminderStorageOptions();
            options.ParseConnectionString(this.connectionStringFixture.ConnectionString);

            return new DynamoDBReminderTable(
                this.loggerFactory,
                this.clusterOptions,
                Options.Create(options));
        }

        protected override Task<string> GetConnectionString()
        {
            return Task.FromResult(AWSTestConstants.IsDynamoDbAvailable ? $"Service={AWSTestConstants.DynamoDbService}" : null!);
        }

        [Fact]
        public void RemindersTable_AWS_Init()
        {
        }

        [Fact]
        public async Task RemindersTable_AWS_RemindersRange()
        {
            await RemindersRange(50);
        }

        [Fact]
        public async Task RemindersTable_AWS_RemindersParallelUpsert()
        {
            await RemindersParallelUpsert();
        }

        [Fact]
        public async Task RemindersTable_AWS_ReminderSimple()
        {
            await ReminderSimple();
        }
    }
}
