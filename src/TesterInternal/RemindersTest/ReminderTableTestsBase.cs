using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using UnitTests.StorageTests;

namespace UnitTests.RemindersTest
{
    [TestClass]
    public abstract class ReminderTableTestsBase
    {
        public TestContext TestContext { get; set; }

        private TraceLogger logger;

        private IReminderTable remindersTable;

        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext = null)
        {
            TraceLogger.Initialize(new NodeConfiguration());

            // Set shorter init timeout for these tests
            OrleansSiloInstanceManager.initTimeout = TimeSpan.FromSeconds(20);
        }

        [TestInitialize]
        public void TestInitialize()
        {
            logger = TraceLogger.GetLogger(GetType().Name, TraceLogger.LoggerType.Application);
            var serviceId = Guid.NewGuid();
            var deploymentId = "test-" + serviceId;

            logger.Info("DeploymentId={0}", deploymentId);

            var globalConfiguration = new GlobalConfiguration
            {
                ServiceId = serviceId,
                DeploymentId = deploymentId,
                AdoInvariantForReminders = GetAdoInvariant(),
                DataConnectionStringForReminders = GetConnectionString()
            };

            var rmndr = CreateRemindersTable();
            rmndr.Init(globalConfiguration, logger).WithTimeout(TimeSpan.FromMinutes(1)).Wait();
            remindersTable = rmndr;
        }

        [TestCleanup]
        public void TestCleanup()
        {
            if (remindersTable != null && SiloInstanceTableTestConstants.DeleteEntriesAfterTest)
            {
                remindersTable.TestOnlyClearTable().Wait();
                remindersTable = null;
            }
            logger.Info("Test {0} completed - Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            // Reset init timeout after tests
            OrleansSiloInstanceManager.initTimeout = AzureTableDefaultPolicies.TableCreationTimeout;
        }

        protected abstract IReminderTable CreateRemindersTable();
        protected abstract string GetConnectionString();

        protected virtual string GetAdoInvariant()
        {
            return null;
        }
        internal async Task RemindersParallelUpsert()
        {
            var upserts = await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            {
                var reminder = CreateReminder(MakeTestGrainReference(), i.ToString());
                return Task.WhenAll(Enumerable.Range(1, 5).Select(j => remindersTable.UpsertRow(reminder)));
            }));
            Assert.IsFalse(upserts.Any(i => i.Distinct().Count() != 5));
        }

        internal async Task ReminderSimple()
        {
            var reminder = CreateReminder(MakeTestGrainReference(), "0");
            await remindersTable.UpsertRow(reminder);

            reminder = await remindersTable.ReadRow(reminder.GrainRef, reminder.ReminderName);

            string etagTemp = reminder.ETag;

            Assert.IsNotNull(etagTemp);

            reminder.ETag = await remindersTable.UpsertRow(reminder);

            var removeRowRes = await remindersTable.RemoveRow(reminder.GrainRef, reminder.ReminderName, etagTemp);
            Assert.IsFalse(removeRowRes, "should have failed. Etag is wrong");
            removeRowRes = await remindersTable.RemoveRow(reminder.GrainRef, "bla", reminder.ETag);
            Assert.IsFalse(removeRowRes, "should have failed. reminder name is wrong");
            removeRowRes = await remindersTable.RemoveRow(reminder.GrainRef, reminder.ReminderName, reminder.ETag);
            Assert.IsTrue(removeRowRes, "should have succeeded. Etag is right");
            removeRowRes = await remindersTable.RemoveRow(reminder.GrainRef, reminder.ReminderName, reminder.ETag);
            Assert.IsFalse(removeRowRes, "should have failed. reminder shouldn't exist");
        }

        internal async Task RemindersRange(int iterations=1000)
        {
            await Task.WhenAll(Enumerable.Range(1, iterations).Select(async i =>
            {
                GrainReference grainRef = MakeTestGrainReference();
                await remindersTable.UpsertRow(CreateReminder(grainRef, i.ToString()));
            }));

            var rows = await remindersTable.ReadRows(0, uint.MaxValue);

            Assert.AreEqual(rows.Reminders.Count, iterations);

            rows = await remindersTable.ReadRows(0, 0);

            Assert.AreEqual(rows.Reminders.Count, iterations);

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

            Assert.IsTrue(returnedSet.SetEquals(expectedSet));
        }

        private static ReminderEntry CreateReminder(GrainReference grainRef, string reminderName)
        {
            return new ReminderEntry
            {
                GrainRef = grainRef,
                Period = TimeSpan.FromMinutes(1),
                StartAt = DateTime.UtcNow.Add(TimeSpan.FromMinutes(1)),
                ReminderName = reminderName
            };
        }

        private static GrainReference MakeTestGrainReference()
        {
            GrainId regularGrainId = GrainId.GetGrainIdForTesting(Guid.NewGuid());
            GrainReference grainRef = GrainReference.FromGrainId(regularGrainId);
            return grainRef;
        }
    }
}
