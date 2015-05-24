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

            membership = AzureBasedMembershipTable.GetMembershipTable(config, true)
                .WaitForResultWithThrow(timeout);
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
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureMembership_ReadAll_0()
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

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureMembership_ReadRow_0()
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

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureMembership_ReadRow_1()
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

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureMembership_ReadAll_1()
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
    }
}
