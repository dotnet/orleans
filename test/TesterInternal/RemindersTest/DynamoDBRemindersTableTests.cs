using System.Threading.Tasks;
using Orleans;
using UnitTests.StorageTests.AWSUtils;
using Xunit;
using OrleansAWSUtils.Reminders;

namespace UnitTests.RemindersTest
{
    public class DynamoDBRemindersTableTests : ReminderTableTestsBase, IClassFixture<DynamoDBStorageTestsFixture>
    {
        public DynamoDBRemindersTableTests(ConnectionStringFixture fixture) : base(fixture)
        {
        }

        protected override IReminderTable CreateRemindersTable()
        {
            if (!AWSTestConstants.CanConnectDynamoDb.Value)
                throw new SkipException("Unable to connect to AWS DynamoDB simulator");

            return new DynamoDBReminderTable();
        }

        protected override string GetConnectionString()
        {
            return $"Service={AWSTestConstants.Service}";
        }

        [SkippableFact, TestCategory("Reminders"), TestCategory("AWS")]
        public void RemindersTable_AWS_Init()
        {
        }

        [SkippableFact, TestCategory("Reminders"), TestCategory("AWS")]
        public async Task RemindersTable_AWS_RemindersRange()
        {
            await RemindersRange(50);
        }

        [SkippableFact, TestCategory("Reminders"), TestCategory("AWS")]
        public async Task RemindersTable_AWS_RemindersParallelUpsert()
        {
            await RemindersParallelUpsert();
        }

        [SkippableFact, TestCategory("Reminders"), TestCategory("AWS")]
        public async Task RemindersTable_AWS_ReminderSimple()
        {
            await ReminderSimple();
        }
    }
}
