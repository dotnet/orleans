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

namespace UnitTests.LivenessTests
{
    [TestClass]
    [DeploymentItem(@"Data\TestDb.mdf")]
    public class MembershipTablePluginTests
    {
        public TestContext TestContext { get; set; }
        private static int counter;
        private static string hostName;

        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext)
        {
            hostName = Dns.GetHostName();

            var cfg = new NodeConfiguration();
            TraceLogger.Initialize(cfg);

            TraceLogger.AddTraceLevelOverride("AzureTableDataManager", Logger.Severity.Verbose3);
            TraceLogger.AddTraceLevelOverride("OrleansSiloInstanceManager", Logger.Severity.Verbose3);
            TraceLogger.AddTraceLevelOverride("Storage", Logger.Severity.Verbose3);

            // Set shorter init timeout for these tests
            OrleansSiloInstanceManager.initTimeout = TimeSpan.FromSeconds(20);

            //Starts the storage emulator if not started already and it exists (i.e. is installed).
            if(!StorageEmulator.TryStart())
            {
                Console.WriteLine("Azure Storage Emulator could not be started.");
            }
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            // Reset init timeout after tests
            OrleansSiloInstanceManager.initTimeout = AzureTableDefaultPolicies.TableCreationTimeout;
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
            var membership = await GetMemebershipTable_Azure();
            Assert.IsNotNull(membership, "Membership Table handler created");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Liveness"), TestCategory("Azure")]
        public async Task MT_ReadAll_Azure()
        {
            var membership = await GetMemebershipTable_Azure();
            await MembershipTable_ReadAll(membership);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Liveness"), TestCategory("Azure")]
        public async Task MT_InsertRow_Azure()
        {
            var membership = await GetMemebershipTable_Azure();
            await MembershipTable_InsertRow(membership);
        }

        [TestMethod, TestCategory("Liveness"), TestCategory("SqlServer")]
        public async Task MT_Init_SqlServer()
        {
            var membership = await GetMemebershipTable_SQL();
            Assert.IsNotNull(membership, "Membership Table handler created");
        }

        [TestMethod, TestCategory("Liveness"), TestCategory("SqlServer")]
        public async Task MT_ReadAll_SqlServer()
        {
            var membership = await GetMemebershipTable_SQL();
            await MembershipTable_ReadAll(membership);
        }

        [TestMethod, TestCategory("Liveness"), TestCategory("SqlServer")]
        public async Task MT_InsertRow_SqlServer()
        {
            var membership = await GetMemebershipTable_SQL();
            await MembershipTable_InsertRow(membership);
        }

        // Test function methods

        private async Task<IMembershipTable> GetMemebershipTable_Azure()
        {
            return await GetMembershipTable(GlobalConfiguration.LivenessProviderType.AzureTable);
        }

        private async Task<IMembershipTable> GetMemebershipTable_SQL()
        {
            return await GetMembershipTable(GlobalConfiguration.LivenessProviderType.SqlServer);
        }


        private async Task MembershipTable_ReadAll(IMembershipTable membership)
        {
            var membershipData = await membership.ReadAll();
            Assert.IsNotNull(membershipData, "Membership Data not null");
        }

        private async Task MembershipTable_InsertRow(IMembershipTable membership)
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

        private async Task<IMembershipTable> GetMembershipTable(GlobalConfiguration.LivenessProviderType membershipType)
        {
            string runId = Guid.NewGuid().ToString("N");

            var config = new GlobalConfiguration { LivenessType = membershipType, DeploymentId = runId };

            IMembershipTable membership;

            switch(membershipType)
            {
                case GlobalConfiguration.LivenessProviderType.AzureTable:
                    config.DataConnectionString = StorageTestConstants.DataConnectionString;
                    membership = await AzureBasedMembershipTable.GetMembershipTable(config, true);
                    break;

                case GlobalConfiguration.LivenessProviderType.SqlServer:
                    config.DataConnectionString = StorageTestConstants.GetSqlConnectionString(TestContext.DeploymentDirectory);
                    membership = await SqlMembershipTable.GetMembershipTable(config, true);
                    break;

                default:
                    throw new NotImplementedException(membershipType.ToString());
            }

            return membership;
        }
    }
}
