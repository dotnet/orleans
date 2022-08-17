using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using TestExtensions;
using UnitTests.MembershipTests;
using Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.TestingHost.Utils;
using Orleans.Internal;

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

        public virtual async Task InitializeAsync()
        {
            await this.remindersTable.Init().WithTimeout(TimeSpan.FromMinutes(1));
        }

        public virtual async Task DisposeAsync()
        {
            if (remindersTable != null && SiloInstanceTableTestConstants.DeleteEntriesAfterTest)
            {
                await remindersTable.TestOnlyClearTable();
            }
        }

        protected abstract IReminderTable CreateRemindersTable();
        protected abstract Task<string> GetConnectionString();

        protected virtual string GetAdoInvariant()
        {
            return null;
        }

        protected async Task RemindersParallelUpsert()
        {
            var upserts = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
            {
                var reminder = CreateReminder(MakeTestGrainReference(), i.ToString());
                return Task.WhenAll(Enumerable.Range(1, 5).Select(j =>
                {
                    return RetryHelper.RetryOnExceptionAsync(5, RetryOperation.Sigmoid, async () =>
                    {
                        return await remindersTable.UpsertRow(reminder);
                    });
                }));
            }));
            Assert.DoesNotContain(upserts, i => i.Distinct().Count() != 5);
        }

        protected async Task ReminderSimple()
        {
            var reminder = CreateReminder(MakeTestGrainReference(), "foo/bar\\#b_a_z?");
            await remindersTable.UpsertRow(reminder);

            var readReminder = await remindersTable.ReadRow(reminder.GrainId, reminder.ReminderName);

            string etagTemp = reminder.ETag = readReminder.ETag;

            Assert.Equal(readReminder.ETag, reminder.ETag);
            Assert.Equal(readReminder.GrainId, reminder.GrainId);
            Assert.Equal(readReminder.Period, reminder.Period);
            Assert.Equal(readReminder.ReminderName, reminder.ReminderName);
            Assert.Equal(readReminder.StartAt, reminder.StartAt);
            Assert.NotNull(etagTemp);

            reminder.ETag = await remindersTable.UpsertRow(reminder);

            var removeRowRes = await remindersTable.RemoveRow(reminder.GrainId, reminder.ReminderName, etagTemp);
            Assert.False(removeRowRes, "should have failed. Etag is wrong");
            removeRowRes = await remindersTable.RemoveRow(reminder.GrainId, "bla", reminder.ETag);
            Assert.False(removeRowRes, "should have failed. reminder name is wrong");
            removeRowRes = await remindersTable.RemoveRow(reminder.GrainId, reminder.ReminderName, reminder.ETag);
            Assert.True(removeRowRes, "should have succeeded. Etag is right");
            removeRowRes = await remindersTable.RemoveRow(reminder.GrainId, reminder.ReminderName, reminder.ETag);
            Assert.False(removeRowRes, "should have failed. reminder shouldn't exist");
        }

        protected async Task RemindersRange(int iterations=1000)
        {
            await Task.WhenAll(Enumerable.Range(1, iterations).Select(async i =>
            {
                var grainRef = MakeTestGrainReference();

                await RetryHelper.RetryOnExceptionAsync(10, RetryOperation.Sigmoid, async () =>
                {
                    await remindersTable.UpsertRow(CreateReminder(grainRef, i.ToString()));
                    return Task.CompletedTask;
                });
            }));

            var rows = await remindersTable.ReadRows(0, uint.MaxValue);

            Assert.Equal(rows.Reminders.Count, iterations);

            rows = await remindersTable.ReadRows(0, 0);

            Assert.Equal(rows.Reminders.Count, iterations);

            var remindersHashes = rows.Reminders.Select(r => r.GrainId.GetUniformHashCode()).ToArray();

            await Task.WhenAll(Enumerable.Range(0, iterations).Select(i =>
            {
                return TestRemindersHashInterval(remindersTable,
                    (uint)Random.Shared.Next(int.MinValue, int.MaxValue),
                    (uint)Random.Shared.Next(int.MinValue, int.MaxValue),
                    remindersHashes);
            }));
        }

        private async Task TestRemindersHashInterval(IReminderTable reminderTable, uint beginHash, uint endHash,
            uint[] remindersHashes)
        {
            var rowsTask = reminderTable.ReadRows(beginHash, endHash);
            var expectedHashes = beginHash < endHash
                ? remindersHashes.Where(r => r > beginHash && r <= endHash)
                : remindersHashes.Where(r => r > beginHash || r <= endHash);

            HashSet<uint> expectedSet = new HashSet<uint>(expectedHashes);
            var returnedHashes = (await rowsTask).Reminders.Select(r => r.GrainId.GetUniformHashCode());
            var returnedSet = new HashSet<uint>(returnedHashes);

            Assert.True(returnedSet.SetEquals(expectedSet));
        }

        private static ReminderEntry CreateReminder(GrainId grainId, string reminderName)
        {
            var now = DateTime.UtcNow;
            now = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);
            return new ReminderEntry
            {
                GrainId = grainId,
                Period = TimeSpan.FromMinutes(1),
                StartAt = now,
                ReminderName = reminderName
            };
        }

        private static GrainId MakeTestGrainReference() => LegacyGrainId.GetGrainId(12345, Guid.NewGuid(), "foo/bar\\#baz?");
    }
}
