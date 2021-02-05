using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
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

            logger.Info("ClusterId={0}", clusterId);
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
                    return RetryHelper.RetryOnExceptionAsync<string>(5, RetryOperation.Sigmoid, async () =>
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

            var readReminder = await remindersTable.ReadRow(reminder.GrainRef, reminder.ReminderName);

            string etagTemp = reminder.ETag = readReminder.ETag;

            Assert.Equal(readReminder.ETag, reminder.ETag);
            Assert.Equal(readReminder.GrainRef.GrainId, reminder.GrainRef.GrainId);
            Assert.Equal(readReminder.Period, reminder.Period);
            Assert.Equal(readReminder.ReminderName, reminder.ReminderName);
            Assert.Equal(readReminder.StartAt, reminder.StartAt);
            Assert.NotNull(etagTemp);

            reminder.ETag = await remindersTable.UpsertRow(reminder);

            var removeRowRes = await remindersTable.RemoveRow(reminder.GrainRef, reminder.ReminderName, etagTemp);
            Assert.False(removeRowRes, "should have failed. Etag is wrong");
            removeRowRes = await remindersTable.RemoveRow(reminder.GrainRef, "bla", reminder.ETag);
            Assert.False(removeRowRes, "should have failed. reminder name is wrong");
            removeRowRes = await remindersTable.RemoveRow(reminder.GrainRef, reminder.ReminderName, reminder.ETag);
            Assert.True(removeRowRes, "should have succeeded. Etag is right");
            removeRowRes = await remindersTable.RemoveRow(reminder.GrainRef, reminder.ReminderName, reminder.ETag);
            Assert.False(removeRowRes, "should have failed. reminder shouldn't exist");
        }

        protected async Task RemindersRange(int iterations=1000)
        {
            await Task.WhenAll(Enumerable.Range(1, iterations).Select(async i =>
            {
                GrainReference grainRef = MakeTestGrainReference();

                await RetryHelper.RetryOnExceptionAsync<Task>(10, RetryOperation.Sigmoid, async () =>
                {
                    await remindersTable.UpsertRow(CreateReminder(grainRef, i.ToString()));
                    return Task.CompletedTask;
                });
            }));

            var rows = await remindersTable.ReadRows(0, uint.MaxValue);

            Assert.Equal(rows.Reminders.Count, iterations);

            rows = await remindersTable.ReadRows(0, 0);

            Assert.Equal(rows.Reminders.Count, iterations);

            var remindersHashes = rows.Reminders.Select(r => r.GrainRef.GetUniformHashCode()).ToArray();

            await Task.WhenAll(Enumerable.Range(0, iterations).Select(i =>
                TestRemindersHashInterval(remindersTable, (uint)ThreadSafeRandom.Next(int.MinValue, int.MaxValue), (uint)ThreadSafeRandom.Next(int.MinValue, int.MaxValue),
                    remindersHashes)));
        }

        private async Task TestRemindersHashInterval(IReminderTable reminderTable, uint beginHash, uint endHash,
            uint[] remindersHashes)
        {
            var rowsTask = reminderTable.ReadRows(beginHash, endHash);
            var expectedHashes = beginHash < endHash
                ? remindersHashes.Where(r => r > beginHash && r <= endHash)
                : remindersHashes.Where(r => r > beginHash || r <= endHash);

            HashSet<uint> expectedSet = new HashSet<uint>(expectedHashes);
            var returnedHashes = (await rowsTask).Reminders.Select(r => r.GrainRef.GetUniformHashCode());
            var returnedSet = new HashSet<uint>(returnedHashes);

            Assert.True(returnedSet.SetEquals(expectedSet));
        }

        private static ReminderEntry CreateReminder(GrainReference grainRef, string reminderName)
        {
            var now = DateTime.UtcNow;
            now = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);
            return new ReminderEntry
            {
                GrainRef = grainRef,
                Period = TimeSpan.FromMinutes(1),
                StartAt = now,
                ReminderName = reminderName
            };
        }

        private GrainReference MakeTestGrainReference()
        {
            GrainId regularGrainId = LegacyGrainId.GetGrainId(12345, Guid.NewGuid(), "foo/bar\\#baz?");
            GrainReference grainRef = (GrainReference)this.ClusterFixture.InternalGrainFactory.GetGrain(regularGrainId);
            return grainRef;
        }
    }
}
