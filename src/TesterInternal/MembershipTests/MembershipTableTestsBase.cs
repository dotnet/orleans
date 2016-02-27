using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using UnitTests.StorageTests;

namespace UnitTests.MembershipTests
{
    [TestClass]
    public abstract class MembershipTableTestsBase
    {
        public TestContext TestContext { get; set; }

        private static readonly string hostName = Dns.GetHostName();

        private TraceLogger logger;

        private IMembershipTable membershipTable;

        private string deploymentId;

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
            deploymentId = "test-" + Guid.NewGuid();

            logger.Info("DeploymentId={0}", deploymentId);

            var globalConfiguration = new GlobalConfiguration
            {
                DeploymentId = deploymentId,
                AdoInvariant = GetAdoInvariant(),
                DataConnectionString = GetConnectionString()
            };

            var mbr = CreateMembershipTable(logger);
            mbr.InitializeMembershipTable(globalConfiguration, true, logger).WithTimeout(TimeSpan.FromMinutes(1)).Wait();
            membershipTable = mbr;
        }

        [TestCleanup]
        public void TestCleanup()
        {
            if (membershipTable != null && SiloInstanceTableTestConstants.DeleteEntriesAfterTest)
            {
                membershipTable.DeleteMembershipTableEntries(deploymentId).Wait();
                membershipTable = null;
            }
            logger.Info("Test {0} completed - Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            // Reset init timeout after tests
            OrleansSiloInstanceManager.initTimeout = AzureTableDefaultPolicies.TableCreationTimeout;
        }
        
        protected abstract IMembershipTable CreateMembershipTable(TraceLogger logger);
        protected abstract string GetConnectionString();

        protected virtual string GetAdoInvariant()
        {
            return null;
        }

        protected async Task MembershipTable_ReadAll_EmptyTable()
        {
            var data = await membershipTable.ReadAll();
            Assert.IsNotNull(data, "Membership Data not null");

            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", data.Version, data);

            Assert.AreEqual(0, data.Members.Count, "Number of records returned - no table version row");
            Assert.IsNotNull(data.Version.VersionEtag, "ETag should not be null");
            Assert.AreEqual(0, data.Version.Version, "Initial tabel version should be zero");
        }

        protected async Task MembershipTable_InsertRow()
        {
            var membershipEntry = CreateMembershipEntryForTest();

            var data = await membershipTable.ReadAll();
            Assert.IsNotNull(data, "Membership Data not null");
            Assert.AreEqual(0, data.Members.Count, "Should be no data initially: {0}", data);

            bool ok = await membershipTable.InsertRow(membershipEntry, data.Version.Next());
            Assert.IsTrue(ok, "InsertRow failed");

            data = await membershipTable.ReadAll();
            Assert.AreEqual(1, data.Members.Count, "Should be one row after insert: {0}", data);
        }

        protected async Task MembershipTable_ReadRow_Insert_Read()
        {
            MembershipTableData data = await membershipTable.ReadAll();
            //TableVersion tableVersion = data.Version;
            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", data.Version, data);

            Assert.AreEqual(0, data.Members.Count, "Number of records returned - no table version row");

            TableVersion newTableVersion = data.Version.Next();
            MembershipEntry newEntry = CreateMembershipEntryForTest();
            bool ok = await membershipTable.InsertRow(newEntry, newTableVersion);

            Assert.IsTrue(ok, "InsertRow failed");

            ok = await membershipTable.InsertRow(newEntry, newTableVersion);
            Assert.IsFalse(ok, "InsertRow should have failed - same entry, old table version");

            ok = await membershipTable.InsertRow(CreateMembershipEntryForTest(), newTableVersion);
            Assert.IsFalse(ok, "InsertRow should have failed - new entry, old table version");
            
            data = await membershipTable.ReadAll();

            var nextTableVersion = data.Version.Next();

            ok = await membershipTable.InsertRow(newEntry, nextTableVersion);
            Assert.IsFalse(ok, "InsertRow should have failed - duplicate entry");

            data = await membershipTable.ReadAll();

            Assert.AreEqual(1, data.Members.Count, "only one row should have been inserted");

            data = await membershipTable.ReadRow(newEntry.SiloAddress);
            Assert.AreEqual(newTableVersion.Version, data.Version.Version);

            logger.Info("Membership.ReadRow returned VableVersion={0} Data={1}", data.Version, data);

            Assert.AreEqual(1, data.Members.Count, "Number of records returned - data row only");

            Assert.IsNotNull(data.Version.VersionEtag, "New version ETag should not be null");
            Assert.AreNotEqual(newTableVersion.VersionEtag, data.Version.VersionEtag, "New VersionEtag differetnfrom last");
            Assert.AreEqual(newTableVersion.Version, data.Version.Version);

            MembershipEntry MembershipEntry = data.Members[0].Item1;
            string eTag = data.Members[0].Item2;
            logger.Info("Membership.ReadRow returned MembershipEntry ETag={0} Entry={1}", eTag, MembershipEntry);

            Assert.IsNotNull(eTag, "ETag should not be null");
            Assert.IsNotNull(MembershipEntry, "MembershipEntry should not be null");
        }

        protected async Task MembershipTable_ReadAll_Insert_ReadAll()
        {
            MembershipTableData data = await membershipTable.ReadAll();
            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", data.Version, data);

            Assert.AreEqual(0, data.Members.Count, "Number of records returned - no table version row");

            TableVersion newTableVersion = data.Version.Next();
            MembershipEntry newEntry = CreateMembershipEntryForTest();
            bool ok = await membershipTable.InsertRow(newEntry, newTableVersion);

            Assert.IsTrue(ok, "InsertRow failed");

            data = await membershipTable.ReadAll();
            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", data.Version, data);

            Assert.AreEqual(1, data.Members.Count, "Number of records returned - data row only");

            Assert.IsNotNull(data.Version.VersionEtag, "New version ETag should not be null");
            Assert.AreNotEqual(newTableVersion.VersionEtag, data.Version.VersionEtag, "New VersionEtag differetnfrom last");
            Assert.AreEqual(newTableVersion.Version, data.Version.Version);

            MembershipEntry MembershipEntry = data.Members[0].Item1;
            string eTag = data.Members[0].Item2;
            logger.Info("Membership.ReadAll returned MembershipEntry ETag={0} Entry={1}", eTag, MembershipEntry);

            Assert.IsNotNull(eTag, "ETag should not be null");
            Assert.IsNotNull(MembershipEntry, "MembershipEntry should not be null");
        }

        protected async Task MembershipTable_UpdateRow()
        {
            var tableData = await membershipTable.ReadAll();

            Assert.IsNotNull(tableData.Version, "TableVersion should not be null");
            Assert.AreEqual(0, tableData.Version.Version, "TableVersion should be zero");
            Assert.AreEqual(0, tableData.Members.Count, "Should be no data initially: {0}", tableData);

            for (int i = 1; i < 10; i++)
            {
                var siloEntry = CreateMembershipEntryForTest();

                TableVersion tableVersion = tableData.Version.Next();
                logger.Info("Calling InsertRow with Entry = {0} TableVersion = {1}", siloEntry, tableVersion);
                bool ok = await membershipTable.InsertRow(siloEntry, tableVersion);

                Assert.IsTrue(ok, "InsertRow failed");

                tableData = await membershipTable.ReadAll();

                var etagBefore = tableData.Get(siloEntry.SiloAddress).Item2;

                Assert.IsNotNull(etagBefore, "ETag should not be null");


                logger.Info("Calling UpdateRow with Entry = {0} correct eTag = {1} old version={2}", siloEntry,
                    etagBefore, tableVersion);

                ok = await membershipTable.UpdateRow(siloEntry, etagBefore, tableVersion);

                Assert.IsFalse(ok, "row update should have failed - Table Data = {0}", tableData);

                tableData = await membershipTable.ReadAll();

                tableVersion = tableData.Version.Next();

                logger.Info("Calling UpdateRow with Entry = {0} correct eTag = {1} correct version={2}", siloEntry,
                    etagBefore, tableVersion);
                ok = await membershipTable.UpdateRow(siloEntry, etagBefore, tableVersion);

                Assert.IsTrue(ok, "UpdateRow failed - Table Data = {0}", tableData);

                
                logger.Info("Calling UpdateRow with Entry = {0} old eTag = {1} old version={2}", siloEntry,
                    etagBefore, tableVersion);

                ok = await membershipTable.UpdateRow(siloEntry, etagBefore, tableVersion);
                
                Assert.IsFalse(ok, "row update should have failed - Table Data = {0}", tableData);

                
                tableData = await membershipTable.ReadAll();

                var etagAfter = tableData.Get(siloEntry.SiloAddress).Item2;

                logger.Info("Calling UpdateRow with Entry = {0} correct eTag = {1} old version={2}", siloEntry,
                    etagAfter, tableVersion);

                ok = await membershipTable.UpdateRow(siloEntry, etagAfter, tableVersion);

                Assert.IsFalse(ok, "row update should have failed - Table Data = {0}", tableData);

                //var nextTableVersion = tableData.Version.Next();

                //logger.Info("Calling UpdateRow with Entry = {0} old eTag = {1} correct version={2}", siloEntry,
                //    etagBefore, nextTableVersion);

                //ok = await membershipTable.UpdateRow(siloEntry, etagBefore, nextTableVersion);

                //Assert.IsFalse(ok, "row update should have failed - Table Data = {0}", tableData);

                tableData = await membershipTable.ReadAll();

                etagBefore = etagAfter;

                etagAfter = tableData.Get(siloEntry.SiloAddress).Item2;

                Assert.AreEqual(etagBefore, etagAfter);
                Assert.IsNotNull(tableData.Version, "TableVersion should not be null");
                Assert.AreEqual(tableVersion.Version, tableData.Version.Version, "TableVersion should be " + tableVersion.Version);
                Assert.AreEqual(i, tableData.Members.Count, "Should be one row after updates: {0}", tableData);
            }
        }

        protected async Task MembershipTable_UpdateRowInParallel()
        {
            var tableData = await membershipTable.ReadAll();

            var data = CreateMembershipEntryForTest();

            var newTableVer = tableData.Version.Next();

            var insertions = Task.WhenAll(Enumerable.Range(1, 20).Select(i => membershipTable.InsertRow(data, newTableVer)));

            Assert.IsTrue((await insertions).Single(x => x), "InsertRow failed");

            await Task.WhenAll(Enumerable.Range(1, 19).Select(async i =>
            {
                bool done;
                do
                {
                    var updatedTableData = await membershipTable.ReadAll();
                    var updatedRow = updatedTableData.Get(data.SiloAddress);
                    var tableVersion = updatedTableData.Version.Next();
                    done = await membershipTable.UpdateRow(updatedRow.Item1, updatedRow.Item2, tableVersion);
                } while (!done);
            })).WithTimeout(TimeSpan.FromSeconds(30));


            tableData = await membershipTable.ReadAll();
            Assert.IsNotNull(tableData.Version, "TableVersion should not be null");
            Assert.AreEqual(20, tableData.Version.Version, "TableVersion should be 20");
            Assert.AreEqual(1, tableData.Members.Count, "Should be one row after insert: {0}", tableData);
        }


        private static int generation;
        // Utility methods
        private static MembershipEntry CreateMembershipEntryForTest()
        {
            SiloAddress siloAddress = SiloAddress.NewLocalAddress(Interlocked.Increment(ref generation));

            var now = DateTime.UtcNow;
            var membershipEntry = new MembershipEntry
            {
                SiloAddress = siloAddress,
                HostName = hostName,
                RoleName = hostName,
                InstanceName = hostName,
                Status = SiloStatus.Joining,
                StartTime = now,
                IAmAliveTime = now
            };

            return membershipEntry;
        }
    }
}
