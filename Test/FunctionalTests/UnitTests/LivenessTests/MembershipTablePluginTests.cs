//#define USE_SQL_SERVER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;

namespace UnitTests.LivenessTests
{
    [TestClass]
    public class MembershipTablePluginTests
    {
        public TestContext TestContext { get; set; }

        private static int _counter;

        private static string hostName;

        private static TraceLogger logger;

        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext)
        {
            hostName = Dns.GetHostName();
            logger = TraceLogger.GetLogger("MembershipTablePluginTests", TraceLogger.LoggerType.Application);

            ClusterConfiguration cfg = new ClusterConfiguration();
            cfg.StandardLoad();
            TraceLogger.Initialize(cfg.GetConfigurationForNode("Primary"));

            TraceLogger.AddTraceLevelOverride("AzureTableDataManager", Logger.Severity.Verbose3);
            TraceLogger.AddTraceLevelOverride("OrleansSiloInstanceManager", Logger.Severity.Verbose3);
            TraceLogger.AddTraceLevelOverride("Storage", Logger.Severity.Verbose3);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            Console.WriteLine("Test {0} completed - Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
        }

        // Test methods 

        [TestMethod, TestCategory("Nightly"), TestCategory("Liveness"), TestCategory("Azure")]
        public async Task MT_Init_Azure()
        {
            var membershipType = GlobalConfiguration.LivenessProviderType.AzureTable;
            IMembershipTable membership = await GetMembershipTable(membershipType);
            Assert.IsNotNull(membership, "Membership Table handler created");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Liveness"), TestCategory("Azure")]
        public async Task MT_ReadAll_Azure()
        {
            var membershipType = GlobalConfiguration.LivenessProviderType.AzureTable;
            IMembershipTable membership = await GetMembershipTable(membershipType);

            await MembershipTable_ReadAll(membership);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Liveness"), TestCategory("Azure")]
        public async Task MT_InsertRow_Azure()
        {
            var membershipType = GlobalConfiguration.LivenessProviderType.AzureTable;
            IMembershipTable membership = await GetMembershipTable(membershipType);

            await MembershipTable_InsertRow(membership);
        }

#if DEBUG
        [TestMethod, TestCategory("Nightly"), TestCategory("Liveness"), TestCategory("Azure")]
        public async Task MT_UpdateRow_Azure()
        {
            var membershipType = GlobalConfiguration.LivenessProviderType.AzureTable;
            IMembershipTable membership = await GetMembershipTable(membershipType);

            await MembershipTable_UpdateRow(membership);
        }
#endif
#if USE_SQL_SERVER || DEBUG
        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Liveness"), TestCategory("SqlServer")]
        public async Task MT_Init_SqlServer()
        {
            var membershipType = GlobalConfiguration.LivenessProviderType.SqlServer;
            IMembershipTable membership = await GetMembershipTable(membershipType);
            Assert.IsNotNull(membership, "Membership Table handler created");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Liveness"), TestCategory("SqlServer")]
        public async Task MT_ReadAll_SqlServer()
        {
            var membershipType = GlobalConfiguration.LivenessProviderType.SqlServer;
            IMembershipTable membership = await GetMembershipTable(membershipType);

            await MembershipTable_ReadAll(membership);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Liveness"), TestCategory("SqlServer")]
        public async Task MT_InsertRow_SqlServer()
        {
            var membershipType = GlobalConfiguration.LivenessProviderType.SqlServer;
            IMembershipTable membership = await GetMembershipTable(membershipType);

            await MembershipTable_InsertRow(membership);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Liveness"), TestCategory("SqlServer")]
        public async Task MT_UpdateRow_SqlServer()
        {
            var membershipType = GlobalConfiguration.LivenessProviderType.SqlServer;
            IMembershipTable membership = await GetMembershipTable(membershipType);

            await MembershipTable_UpdateRow(membership);
        }
#endif
        // Test function methods

        private async Task MembershipTable_ReadAll(IMembershipTable membership)
        {
            MembershipTableData MembershipData = await membership.ReadAll();
            Assert.IsNotNull(MembershipData, "Membership Data not null");
        }

        private async Task MembershipTable_InsertRow(IMembershipTable membership)
        {
            MembershipEntry MembershipEntry = CreateMembershipEntryForTest();

            MembershipTableData MembershipData = await membership.ReadAll();
            Assert.IsNotNull(MembershipData, "Membership Data not null");
            Assert.AreEqual(0, MembershipData.Members.Count, "Should be no data initially: {0}", MembershipData);

            bool ok = await membership.InsertRow(MembershipEntry, MembershipData.Version);
            Assert.IsTrue(ok, "InsertRow OK");

            MembershipData = await membership.ReadAll();
            Assert.AreEqual(1, MembershipData.Members.Count, "Should be one row after insert: {0}", MembershipData);
        }

        private async Task MembershipTable_UpdateRow(IMembershipTable membership)
        {
            MembershipEntry MembershipEntry = CreateMembershipEntryForTest();

            MembershipTableData MembershipData = await membership.ReadAll();
            TableVersion tableVer = MembershipData.Version;
            Assert.AreEqual(0, MembershipData.Members.Count, "Should be no data initially: {0}", MembershipData);

            logger.Info("Calling InsertRow with Entry = {0} TableVersion = {1}", MembershipEntry, tableVer);
            bool ok = await membership.InsertRow(MembershipEntry, tableVer);

            Assert.IsTrue(ok, "InsertRow OK");

            MembershipData = await membership.ReadAll();
            Assert.AreEqual(1, MembershipData.Members.Count, "Should be one row after insert: {0}", MembershipData);

            Tuple<MembershipEntry, string> newEntryData = MembershipData.Get(MembershipEntry.SiloAddress);
            string eTag = newEntryData.Item2;
            Assert.IsNotNull(eTag, "ETag should not be null");

            tableVer = MembershipData.Version;
            Assert.IsNotNull(tableVer, "TableVersion should not be null");
            tableVer = tableVer.Next();

            MembershipEntry = CreateMembershipEntryForTest();
            MembershipEntry.Status = SiloStatus.Active;

            logger.Info("Calling UpdateRow with Entry = {0} eTag = {1} New TableVersion={2}", MembershipEntry, eTag, tableVer);
            ok = await membership.UpdateRow(MembershipEntry, eTag, tableVer);

            MembershipData = await membership.ReadAll();
            Assert.AreEqual(1, MembershipData.Members.Count, "Should be one row after update: {0}", MembershipData);

            Assert.IsTrue(ok, "UpdateRow OK - Table Data = {0}", MembershipData);
        }

        // Utility methods

        private static MembershipEntry CreateMembershipEntryForTest()
        {
            SiloAddress siloAddress = SiloAddress.NewLocalAddress(_counter++);

            DateTime now = DateTime.UtcNow;

            MembershipEntry MembershipEntry = new MembershipEntry
            {
                SiloAddress = siloAddress,
                HostName = hostName,
                RoleName = hostName,
                InstanceName = hostName,
                Status = SiloStatus.Joining,
                StartTime = now,
                IAmAliveTime = now
            };

            return MembershipEntry;
        }

        private async Task<IMembershipTable> GetMembershipTable(GlobalConfiguration.LivenessProviderType membershipType)
        {
            string runId = Guid.NewGuid().ToString("N");

            var config = new GlobalConfiguration();
            config.LivenessType = membershipType;
            config.DeploymentId = runId;

            IMembershipTable membership;

            if (membershipType == GlobalConfiguration.LivenessProviderType.AzureTable)
            {
                config.DataConnectionString = TestConstants.DataConnectionString;
                membership = await AzureBasedMembershipTable.GetMembershipTable(config, true);
            }
            else if (membershipType == GlobalConfiguration.LivenessProviderType.SqlServer)
            {
                config.DataConnectionString = TestConstants.GetSqlConnectionString(TestContext);
                membership = await SqlMembershipTable.GetMembershipTable(config, true);
            }
            else
            {
                throw new NotImplementedException(membershipType.ToString());
            }

            return membership;
        }
    }
}
