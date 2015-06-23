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
using System.Threading.Tasks;
using NUnit.Framework;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.StorageTests;


namespace UnitTests.MembershipTests
{
    /// <summary>
    /// Tests for operation of Orleans SiloInstanceManager using ZookeeperStore - Requires access to external Zookeeper storage
    /// </summary>
    [TestFixture]
    public class ZookeeperMembershipTableTests
    {
        private string deploymentId;
        private SiloAddress siloAddress;
        private IMembershipTable membership;
        private static readonly TimeSpan timeout = TimeSpan.FromMinutes(1);
        private readonly TraceLogger logger = TraceLogger.GetLogger("ZookeeperMembershipTableTests", TraceLogger.LoggerType.Application);


        [TestFixtureSetUp]
        public void ClassInitialize()
        {
            TraceLogger.Initialize(new NodeConfiguration());
        }

        [SetUp]
        public void TestInitialize()
        {
            Initialize().Wait();
        }

        [TearDown]
        public void TestCleanup()
        {
            if (membership != null && SiloInstanceTableTestConstants.DeleteEntriesAfterTest)
            {
                membership.DeleteMembershipTableEntries(deploymentId).Wait();
                membership = null;
            }
        }

        private async Task Initialize()
        {
            deploymentId = "test-" + Guid.NewGuid();
            int generation = SiloAddress.AllocateNewGeneration();
            siloAddress = SiloAddress.NewLocalAddress(generation);

            logger.Info("DeploymentId={0} Generation={1}", deploymentId, generation);

            GlobalConfiguration config = new GlobalConfiguration
            {
                DeploymentId = deploymentId,
                DataConnectionString = StorageTestConstants.GetZooKeeperConnectionString()
            };

            var mbr = AssemblyLoader.LoadAndCreateInstance<IMembershipTable>(Constants.ORLEANS_ZOOKEEPER_UTILS_DLL, logger);
            await mbr.InitializeMembershipTable(config, true, logger).WithTimeout(timeout);
            membership = mbr;
        }

        [Test, Category("Membership"), Category("ZooKeeper")]
        public void MembershipTable_ZooKeeper_Init()
        {
            Assert.IsNotNull(membership, "Membership Table handler created");
        }

        [Test, Category("Membership"), Category("ZooKeeper")]
        public async Task MembershipTable_ZooKeeper_ReadAll_EmptyTable()
        {
            await MembershipTablePluginTests.MembershipTable_ReadAll_EmptyTable(membership);
        }

        [Test, Category("Membership"), Category("ZooKeeper")]
        public async Task MembershipTable_ZooKeeper_InsertRow()
        {
            await MembershipTablePluginTests.MembershipTable_InsertRow(membership);
        }

        [Test, Category("Membership"), Category("ZooKeeper")]
        public async Task MembershipTable_ZooKeeper_ReadRow_Insert_Read()
        {
            await Initialize();
            await MembershipTablePluginTests.MembershipTable_ReadRow_Insert_Read(membership);
        }

        [Test, Category("Membership"), Category("ZooKeeper")]
        public async Task MembershipTable_ZooKeeper_ReadAll_Insert_ReadAll()
        {
            await MembershipTablePluginTests.MembershipTable_ReadAll_Insert_ReadAll(membership);
        }

        [Test, Category("Membership"), Category("ZooKeeper")]
        public async Task MembershipTable_ZooKeeper_UpdateRow()
        {
            await MembershipTablePluginTests.MembershipTable_UpdateRow(membership);
        }
    }
}
