/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.TestingHost;

namespace UnitTests.StorageTests
{
    [TestClass]
    [DeploymentItem(@"Data\TestDb.mdf")]
    public class MembershipTablePluginTests
    {
        public TestContext TestContext { get; set; }
        private static int counter;
        private static string hostName = Dns.GetHostName();
        private static readonly TraceLogger logger = TraceLogger.GetLogger("MembershipTablePluginTests");

        // Test methods 

        [TestMethod, TestCategory("Liveness"), TestCategory("SqlServer")]
        public async Task MT_Init_SqlServer()
        {
            var membership = await GetMemebershipTable_SQL(TestContext.DeploymentDirectory);
            Assert.IsNotNull(membership, "Membership Table handler created");
        }

        [TestMethod, TestCategory("Liveness"), TestCategory("SqlServer")]
        public async Task MT_ReadAll_SqlServer()
        {
            var membership = await GetMemebershipTable_SQL(TestContext.DeploymentDirectory);
            await MembershipTable_ReadAll(membership);
        }

        [TestMethod, TestCategory("Liveness"), TestCategory("SqlServer")]
        public async Task MT_InsertRow_SqlServer()
        {
            var membership = await GetMemebershipTable_SQL(TestContext.DeploymentDirectory);
            await MembershipTable_InsertRow(membership);
        }

        [TestMethod, TestCategory("Liveness"), TestCategory("ZooKeeper")]
        public async Task MT_Init_ZooKeeper()
        {
            var membership = await GetMembershipTable_ZooKeeper();
            Assert.IsNotNull(membership, "Membership Table handler created");
        }

        [TestMethod, TestCategory("Liveness"), TestCategory("ZooKeeper")]
        public async Task MT_ReadAll_ZooKeeper()
        {
            var membership = await GetMembershipTable_ZooKeeper();
            await MembershipTable_ReadAll(membership);
        }

        [TestMethod, TestCategory("Liveness"), TestCategory("ZooKeeper")]
        public async Task MT_InsertRow_ZooKeeper()
        {
            var membership = await GetMembershipTable_ZooKeeper();
            await MembershipTable_InsertRow(membership);
        }

        // Test function methods

        internal static async Task<IMembershipTable> GetMemebershipTable_Azure()
        {
            return await GetMembershipTable(GlobalConfiguration.LivenessProviderType.AzureTable);
        }

        internal static async Task<IMembershipTable> GetMemebershipTable_SQL(string deploymentDirectory)
        {
            return await GetMembershipTable(GlobalConfiguration.LivenessProviderType.SqlServer, deploymentDirectory);
        }

        internal static async Task<IMembershipTable> GetMembershipTable_ZooKeeper()
        {
            return await GetMembershipTable(GlobalConfiguration.LivenessProviderType.ZooKeeper);
        }

        internal static async Task MembershipTable_ReadAll(IMembershipTable membership)
        {
            var membershipData = await membership.ReadAll();
            Assert.IsNotNull(membershipData, "Membership Data not null");
        }

        internal static async Task MembershipTable_InsertRow(IMembershipTable membership)
        {
            var membershipEntry = CreateMembershipEntryForTest();

            var membershipData = await membership.ReadAll();
            Assert.IsNotNull(membershipData, "Membership Data not null");
            Assert.AreEqual(0, membershipData.Members.Count, "Should be no data initially: {0}", membershipData);

            bool ok = await membership.InsertRow(membershipEntry, membershipData.Version);
            Assert.IsTrue(ok, "InsertRow OK");

            membershipData = await membership.ReadAll();
            Assert.AreEqual(1, membershipData.Members.Count, "Should be one row after insert: {0}", membershipData);
        }

        internal static async Task MembershipTable_ReadAll_0(IMembershipTable membership)
        {
            MembershipTableData data = await membership.ReadAll();
            TableVersion tableVersion = data.Version;
            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", tableVersion, data);

            Assert.AreEqual(0, data.Members.Count, "Number of records returned - no table version row");

            string eTag = tableVersion.VersionEtag;
            int ver = tableVersion.Version;

            Assert.IsNotNull(eTag, "ETag should not be null");
            Assert.AreEqual(0, ver, "Initial tabel version should be zero");
        }

        internal static async Task MembershipTable_ReadRow_0(IMembershipTable membership, SiloAddress siloAddress)
        {
            MembershipTableData data = await membership.ReadRow(siloAddress);
            TableVersion tableVersion = data.Version;
            logger.Info("Membership.ReadRow returned VableVersion={0} Data={1}", tableVersion, data);

            Assert.AreEqual(0, data.Members.Count, "Number of records returned - no table version row");

            string eTag = tableVersion.VersionEtag;
            int ver = tableVersion.Version;

            logger.Info("Membership.ReadRow returned MembershipEntry ETag={0} TableVersion={1}", eTag, tableVersion);

            Assert.IsNotNull(eTag, "ETag should not be null");
            Assert.AreEqual(0, ver, "Initial tabel version should be zero");
        }

        internal static async Task MembershipTable_ReadRow_1(IMembershipTable membership, SiloAddress siloAddress)
        {
            MembershipTableData data = await membership.ReadAll();
            TableVersion tableVersion = data.Version;
            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", tableVersion, data);

            Assert.AreEqual(0, data.Members.Count, "Number of records returned - no table version row");

            DateTime now = DateTime.UtcNow;
            MembershipEntry entry = new MembershipEntry
            {
                SiloAddress = siloAddress,
                StartTime = now,
                Status = SiloStatus.Active,
            };

            TableVersion newTableVersion = tableVersion.Next();
            bool ok = await membership.InsertRow(entry, newTableVersion);

            Assert.IsTrue(ok, "InsertRow completed successfully");

            data = await membership.ReadRow(siloAddress);
            tableVersion = data.Version;
            logger.Info("Membership.ReadRow returned VableVersion={0} Data={1}", tableVersion, data);

            Assert.AreEqual(1, data.Members.Count, "Number of records returned - data row only");

            Assert.IsNotNull(tableVersion.VersionEtag, "New version ETag should not be null");
            Assert.AreNotEqual(newTableVersion.VersionEtag, tableVersion.VersionEtag, "New VersionEtag differetnfrom last");
            Assert.AreEqual(newTableVersion.Version, tableVersion.Version, "New table version number");

            MembershipEntry MembershipEntry = data.Members[0].Item1;
            string eTag = data.Members[0].Item2;
            logger.Info("Membership.ReadRow returned MembershipEntry ETag={0} Entry={1}", eTag, MembershipEntry);

            Assert.IsNotNull(eTag, "ETag should not be null");
            Assert.IsNotNull(MembershipEntry, "MembershipEntry should not be null");
        }

        internal static async Task MembershipTable_ReadAll_1(IMembershipTable membership, SiloAddress siloAddress)
        {
            MembershipTableData data = await membership.ReadAll();
            TableVersion tableVersion = data.Version;
            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", tableVersion, data);

            Assert.AreEqual(0, data.Members.Count, "Number of records returned - no table version row");

            DateTime now = DateTime.UtcNow;
            MembershipEntry entry = new MembershipEntry
            {
                SiloAddress = siloAddress,
                StartTime = now,
                Status = SiloStatus.Active,
            };

            TableVersion newTableVersion = tableVersion.Next();
            bool ok = await membership.InsertRow(entry, newTableVersion);

            Assert.IsTrue(ok, "InsertRow completed successfully");

            data = await membership.ReadAll();
            tableVersion = data.Version;
            logger.Info("Membership.ReadAll returned VableVersion={0} Data={1}", tableVersion, data);

            Assert.AreEqual(1, data.Members.Count, "Number of records returned - data row only");

            Assert.IsNotNull(tableVersion.VersionEtag, "New version ETag should not be null");
            Assert.AreNotEqual(newTableVersion.VersionEtag, tableVersion.VersionEtag, "New VersionEtag differetnfrom last");
            Assert.AreEqual(newTableVersion.Version, tableVersion.Version, "New table version number");

            MembershipEntry MembershipEntry = data.Members[0].Item1;
            string eTag = data.Members[0].Item2;
            logger.Info("Membership.ReadAll returned MembershipEntry ETag={0} Entry={1}", eTag, MembershipEntry);

            Assert.IsNotNull(eTag, "ETag should not be null");
            Assert.IsNotNull(MembershipEntry, "MembershipEntry should not be null");
        }
        // Utility methods

        private static MembershipEntry CreateMembershipEntryForTest()
        {
            var siloAddress = SiloAddress.NewLocalAddress(counter++);

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

        private static async Task<IMembershipTable> GetMembershipTable(GlobalConfiguration.LivenessProviderType membershipType, 
            string deploymentDirectory = null)
        {
            string runId = Guid.NewGuid().ToString("N");

            var config = new GlobalConfiguration { LivenessType = membershipType, DeploymentId = runId };

            IMembershipTable membership;

            switch(membershipType)
            {
                case GlobalConfiguration.LivenessProviderType.AzureTable:
                    config.DataConnectionString = StorageTestConstants.DataConnectionString;
                    membership = new AzureBasedMembershipTable();
                    break;

                case GlobalConfiguration.LivenessProviderType.SqlServer:
                    config.DataConnectionString = StorageTestConstants.GetSqlConnectionString(deploymentDirectory);
                    membership = new SqlMembershipTable();
                    break;

                case GlobalConfiguration.LivenessProviderType.ZooKeeper:
                    config.DataConnectionString = StorageTestConstants.GetZooKeeperConnectionString();
                    membership = AssemblyLoader.LoadAndCreateInstance<IMembershipTable>(Constants.ORLEANS_ZOOKEEPER_UTILS_DLL, logger);
                    break;

                default:
                    throw new NotImplementedException(membershipType.ToString());
            }

            await membership.InitializeMembershipTable(config, true, TraceLogger.GetLogger(membership.GetType().Name));
            return membership;
        }
    }
}
