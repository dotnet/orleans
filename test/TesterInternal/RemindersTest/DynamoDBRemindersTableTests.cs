using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
            return new DynamoDBReminderTable();
        }

        protected override string GetConnectionString()
        {
            return $"Service={AWSTestConstants.Service}";
        }

        [Fact, TestCategory("Reminders"), TestCategory("AWS")]
        public void RemindersTable_AWS_Init()
        {
        }

        [Fact, TestCategory("Reminders"), TestCategory("AWS")]
        public async Task RemindersTable_AWS_RemindersRange()
        {
            await RemindersRange(50);
        }

        [Fact, TestCategory("Reminders"), TestCategory("AWS")]
        public async Task RemindersTable_AWS_RemindersParallelUpsert()
        {
            await RemindersParallelUpsert();
        }

        [Fact, TestCategory("Reminders"), TestCategory("AWS")]
        public async Task RemindersTable_AWS_ReminderSimple()
        {
            await ReminderSimple();
        }
    }
}
