using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using TestExtensions;
using UnitTests.MembershipTests;
using Xunit;

namespace UnitTests.RemindersTest
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public abstract class ReminderTableTestsBase : IDisposable, IClassFixture<ConnectionStringFixture>
    {
        protected readonly TestEnvironmentFixture ClusterFixture;
        private readonly Logger logger;

        private readonly IReminderTable remindersTable;

        protected const string testDatabaseName = "OrleansReminderTest";//for relational storage
        
        protected ReminderTableTestsBase(ConnectionStringFixture fixture, TestEnvironmentFixture clusterFixture)
        {
            this.ClusterFixture = clusterFixture;
            LogManager.Initialize(new NodeConfiguration());
            
            logger = LogManager.GetLogger(GetType().Name, LoggerType.Application);
            var serviceId = Guid.NewGuid();
            var deploymentId = "test-" + serviceId;

            logger.Info("DeploymentId={0}", deploymentId);

            fixture.InitializeConnectionStringAccessor(GetConnectionString);

            var globalConfiguration = new GlobalConfiguration
            {
                ServiceId = serviceId,
                DeploymentId = deploymentId,
                AdoInvariantForReminders = GetAdoInvariant(),
                DataConnectionStringForReminders = fixture.ConnectionString
            };

            var rmndr = CreateRemindersTable();
            rmndr.Init(globalConfiguration, logger).WithTimeout(TimeSpan.FromMinutes(1)).Wait();
            remindersTable = rmndr;
        }

        public virtual void Dispose()
        {
            if (remindersTable != null && SiloInstanceTableTestConstants.DeleteEntriesAfterTest)
            {
                remindersTable.TestOnlyClearTable().Wait();
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
            var upserts = await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            {
                var reminder = CreateReminder(MakeTestGrainReference(), i.ToString());
                return Task.WhenAll(Enumerable.Range(1, 5).Select(j => remindersTable.UpsertRow(reminder)));
            }));
            Assert.False(upserts.Any(i => i.Distinct().Count() != 5));
        }

        protected async Task ReminderSimple()
        {
            var reminder = CreateReminder(MakeTestGrainReference(), "0");
            await remindersTable.UpsertRow(reminder);

            var readReminder = await remindersTable.ReadRow(reminder.GrainRef, reminder.ReminderName);
            
            string etagTemp = reminder.ETag = readReminder.ETag;

            Assert.Equal(JsonConvert.SerializeObject(readReminder), JsonConvert.SerializeObject(reminder));

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
                await remindersTable.UpsertRow(CreateReminder(grainRef, i.ToString()));
            }));

            var rows = await remindersTable.ReadRows(0, uint.MaxValue);

            Assert.Equal(rows.Reminders.Count, iterations);

            rows = await remindersTable.ReadRows(0, 0);

            Assert.Equal(rows.Reminders.Count, iterations);

            var remindersHashes = rows.Reminders.Select(r => r.GrainRef.GetUniformHashCode()).ToArray();
            
            SafeRandom random = new SafeRandom();

            await Task.WhenAll(Enumerable.Range(0, iterations).Select(i =>
                TestRemindersHashInterval(remindersTable, (uint)random.Next(), (uint)random.Next(),
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
            GrainId regularGrainId = GrainId.GetGrainIdForTesting(Guid.NewGuid());
            GrainReference grainRef = this.ClusterFixture.InternalGrainFactory.GetGrain(regularGrainId);
            return grainRef;
        }
    }
}
