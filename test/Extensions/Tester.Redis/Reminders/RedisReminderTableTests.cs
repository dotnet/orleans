using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Reminders.Redis;
using Orleans.Runtime;
using Orleans.TestingHost;
using Tester.Redis.Utility;
using TestExtensions;
using UnitTests;
using UnitTests.GrainInterfaces;
using UnitTests.RemindersTest;
using Xunit;

namespace Tester.Redis.Reminders
{
    [TestCategory("Redis"), TestCategory("Reminders"), TestCategory("Functional")]
    public class RedisRemindersTableTests : ReminderTableTestsBase
    {
        public RedisRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture clusterFixture) : base (fixture, clusterFixture, CreateFilters())
        {
        }

        private static LoggerFilterOptions CreateFilters()
        {
            LoggerFilterOptions filters = new LoggerFilterOptions();
            filters.AddFilter(nameof(RedisRemindersTableTests), LogLevel.Trace);
            return filters;
        }

        protected override IReminderTable CreateRemindersTable()
        {
            TestUtils.CheckForRedis();

            RedisReminderTable reminderTable = new(
                this.loggerFactory.CreateLogger<RedisReminderTable>(),
                this.clusterOptions,
                Options.Create(new RedisReminderTableOptions() {  ConnectionString = GetConnectionString().Result })
                );

            if (reminderTable == null)
            {
                throw new InvalidOperationException("RedisReminderTable not configured");
            }

            return reminderTable;
        }
        protected override Task<string> GetConnectionString() => Task.FromResult(TestDefaultConfiguration.RedisConnectionString);

        [SkippableFact]
        public void RemindersTable_Redis_Init()
        {
        }

        [SkippableFact]
        public async Task RemindersTable_Redis_RemindersRange()
        {
            await RemindersRange(iterations: 50);
        }

        [SkippableFact]
        public async Task RemindersTable_Redis_RemindersParallelUpsert()
        {
            await RemindersParallelUpsert();
        }

        [SkippableFact]
        public async Task RemindersTable_Redis_ReminderSimple()
        {
            await ReminderSimple();
        }
    }
}
