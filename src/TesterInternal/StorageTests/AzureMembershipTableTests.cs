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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.TestingHost;
using Orleans.AzureUtils;


namespace UnitTests.StorageTests
{
    /// <summary>
    /// Tests for operation of Orleans SiloInstanceManager using AzureStore - Requires access to external Azure storage
    /// </summary>
    [TestClass]
    public class AzureMembershipTableTests
    {
        public TestContext TestContext { get; set; }

        private string deploymentId;
        private int generation;
        private SiloAddress siloAddress;
        private AzureBasedMembershipTable membership;
        private static readonly TimeSpan timeout = TimeSpan.FromMinutes(1);
        private readonly TraceLogger logger;

        public AzureMembershipTableTests()
        {
            logger = TraceLogger.GetLogger("AzureMembershipTableTests", TraceLogger.LoggerType.Application);
        }

        // Use ClassInitialize to run code before running the first test in the class
        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext)
        {
            TraceLogger.Initialize(new NodeConfiguration());
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

        // Use TestInitialize to run code before running each test 
        [TestInitialize]
        public void TestInitialize()
        {
            deploymentId = "test-" + Guid.NewGuid();
            generation = SiloAddress.AllocateNewGeneration();
            siloAddress = SiloAddress.NewLocalAddress(generation);

            logger.Info("DeploymentId={0} Generation={1}", deploymentId, generation);

            GlobalConfiguration config = new GlobalConfiguration
            {
                DeploymentId = deploymentId,
                DataConnectionString = StorageTestConstants.DataConnectionString
            };

            membership = new AzureBasedMembershipTable();
            membership.InitializeMembershipTable(config, true, logger).WaitWithThrow(timeout);
        }

        // Use TestCleanup to run code after each test has run
        [TestCleanup]
        public void TestCleanup()
        {
            if (membership != null && SiloInstanceTableTestConstants.DeleteEntriesAfterTest)
            {
                membership.DeleteMembershipTableEntries(deploymentId).Wait();
                membership = null;
            }
            Console.WriteLine("Test {0} completed - Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            // Reset init timeout after tests
            OrleansSiloInstanceManager.initTimeout = AzureTableDefaultPolicies.TableCreationTimeout;
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Liveness"), TestCategory("Azure")]
        public async Task MT_Init_Azure()
        {
            var membership = await MembershipTablePluginTests.GetMemebershipTable_Azure();
            Assert.IsNotNull(membership, "Membership Table handler created");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Liveness"), TestCategory("Azure")]
        public async Task MT_ReadAll_Azure()
        {
            var membership = await MembershipTablePluginTests.GetMemebershipTable_Azure();
            await MembershipTablePluginTests.MembershipTable_ReadAll(membership);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Liveness"), TestCategory("Azure")]
        public async Task MT_InsertRow_Azure()
        {
            var membership = await MembershipTablePluginTests.GetMemebershipTable_Azure();
            await MembershipTablePluginTests.MembershipTable_InsertRow(membership);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureMembership_ReadAll_0()
        {
            var membership = await MembershipTablePluginTests.GetMemebershipTable_Azure();
            await MembershipTablePluginTests.MembershipTable_ReadAll_0(membership);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureMembership_ReadRow_0()
        {
            var membership = await MembershipTablePluginTests.GetMemebershipTable_Azure();
            await MembershipTablePluginTests.MembershipTable_ReadRow_0(membership, siloAddress);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureMembership_ReadRow_1()
        {
            var membership = await MembershipTablePluginTests.GetMemebershipTable_Azure();
            await MembershipTablePluginTests.MembershipTable_ReadRow_1(membership, siloAddress);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureMembership_ReadAll_1()
        {
            var membership = await MembershipTablePluginTests.GetMemebershipTable_Azure();
            await MembershipTablePluginTests.MembershipTable_ReadAll_1(membership, siloAddress);
        }
    }
}
