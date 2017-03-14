using System.Threading.Tasks;
using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using OrleansAWSUtils.Reminders;
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
        public DynamoDBRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment)
        {
        }

        protected override IReminderTable CreateRemindersTable()
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
                throw new SkipException("Unable to connect to AWS DynamoDB simulator");

            return new DynamoDBReminderTable(this.ClusterFixture.Services.GetRequiredService<IGrainReferenceConverter>());
        }

        protected override Task<string> GetConnectionString()
        {
            return Task.FromResult(AWSTestConstants.IsDynamoDbAvailable ? $"Service={AWSTestConstants.Service}" : null);
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
