using System.Threading.Tasks;
using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Orleans;
using Orleans.Configuration;
using Orleans.Reminders.DynamoDB;
using Orleans.Runtime;
using TestExtensions;
using UnitTests;
using UnitTests.RemindersTest;
using Xunit;

namespace AWSUtils.Tests.RemindersTest
{
    [TestCategory("Reminders"), TestCategory("AWS"), TestCategory("DynamoDb")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class DynamoDBRemindersTableTests : ReminderTableTestsBase, IClassFixture<DynamoDBStorageTestsFixture>
    {
        public DynamoDBRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, new LoggerFilterOptions())
        {
        }

        protected override IReminderTable CreateRemindersTable()
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
                throw new SkipException("Unable to connect to AWS DynamoDB simulator");

            var options = new DynamoDBReminderStorageOptions();
            options.ParseConnectionString(this.connectionStringFixture.ConnectionString);

            return new DynamoDBReminderTable(
                this.loggerFactory,
                this.clusterOptions,
                Options.Create(options));
        }

        protected override Task<string> GetConnectionString()
        {
            return Task.FromResult(AWSTestConstants.IsDynamoDbAvailable ? $"Service={AWSTestConstants.DynamoDbService}" : null);
        }

        [SkippableFact]
        public void RemindersTable_AWS_Init()
        {
        }

        [SkippableFact]
        public async Task RemindersTable_AWS_RemindersRange()
        {
            await RemindersRange(50);
        }

        [SkippableFact]
        public async Task RemindersTable_AWS_RemindersParallelUpsert()
        {
            await RemindersParallelUpsert();
        }

        [SkippableFact]
        public async Task RemindersTable_AWS_ReminderSimple()
        {
            await ReminderSimple();
        }
    }
}
