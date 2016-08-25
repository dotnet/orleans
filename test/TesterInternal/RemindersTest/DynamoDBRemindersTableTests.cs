using System.Threading.Tasks;
using Orleans;
using OrleansAWSUtils.Reminders;
using UnitTests.StorageTests.AWSUtils;
using Xunit;

namespace UnitTests.RemindersTest
{
    [TestCategory("Reminders"), TestCategory("AWS"), TestCategory("DynamoDb")]
    public class DynamoDBRemindersTableTests : ReminderTableTestsBase, IClassFixture<DynamoDBStorageTestsFixture>
    {
        public DynamoDBRemindersTableTests(ConnectionStringFixture fixture) : base(fixture)
        {
        }

        protected override IReminderTable CreateRemindersTable()
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
                throw new SkipException("Unable to connect to AWS DynamoDB simulator");

            return new DynamoDBReminderTable();
        }

        protected override string GetConnectionString()
        {
            return $"Service={AWSTestConstants.Service}";
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
