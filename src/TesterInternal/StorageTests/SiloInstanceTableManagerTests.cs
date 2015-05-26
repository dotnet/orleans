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
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;

namespace UnitTests.StorageTests
{
    internal static class SiloInstanceTableTestConstants
    {
        internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

        internal static readonly bool DeleteEntriesAfterTest = true; // false; // Set to false for Debug mode

        internal static readonly string INSTANCE_STATUS_CREATED = SiloStatus.Created.ToString();  //"Created";
        internal static readonly string INSTANCE_STATUS_ACTIVE = SiloStatus.Active.ToString();    //"Active";
        internal static readonly string INSTANCE_STATUS_DEAD = SiloStatus.Dead.ToString();        //"Dead";
    }

    /// <summary>
    /// Tests for operation of Orleans SiloInstanceManager using AzureStore - Requires access to external Azure storage
    /// </summary>
    [TestClass]
    public class SiloInstanceTableManagerTests
    {
        public TestContext TestContext { get; set; }

        private string deploymentId;
        private int generation;
        private SiloAddress siloAddress;
        private SiloInstanceTableEntry myEntry;
        private OrleansSiloInstanceManager manager;
        private readonly TraceLogger logger;

        public SiloInstanceTableManagerTests()
        {
            logger = TraceLogger.GetLogger("SiloInstanceTableManagerTests", TraceLogger.LoggerType.Application);
        }

        // Use ClassInitialize to run code before running the first test in the class
        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext)
        {
            TraceLogger.Initialize(new NodeConfiguration());
        }

        // Use TestInitialize to run code before running each test 
        [TestInitialize]
        public void TestInitialize()
        {
            deploymentId = "test-" + Guid.NewGuid();
            generation = SiloAddress.AllocateNewGeneration();
            siloAddress = SiloAddress.NewLocalAddress(generation);

            logger.Info("DeploymentId={0} Generation={1}", deploymentId, generation);

            logger.Info("Initializing SiloInstanceManager");
            manager = OrleansSiloInstanceManager.GetManager(deploymentId, StorageTestConstants.DataConnectionString)
                .WaitForResultWithThrow(SiloInstanceTableTestConstants.Timeout);
        }

        // Use TestCleanup to run code after each test has run
        [TestCleanup]
        public void TestCleanup()
        {
            if (manager != null && SiloInstanceTableTestConstants.DeleteEntriesAfterTest)
            {
                TimeSpan timeout = SiloInstanceTableTestConstants.Timeout;

                logger.Info("TestCleanup Timeout={0}", timeout);

                manager.DeleteTableEntries(deploymentId).WaitWithThrow(timeout);

                logger.Info("TestCleanup -  Finished");
                manager = null;
            }
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public void SiloInstanceTable_Op_RegisterSiloInstance()
        {
            RegisterSiloInstance();
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public void SiloInstanceTable_Op_ActivateSiloInstance()
        {
            RegisterSiloInstance();

            manager.ActivateSiloInstance(myEntry);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public void SiloInstanceTable_Op_UnregisterSiloInstance()
        {
            RegisterSiloInstance();

            manager.UnregisterSiloInstance(myEntry);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task SiloInstanceTable_Op_CreateSiloEntryConditionally()
        {
            bool didInsert = await manager.TryCreateTableVersionEntryAsync()
                .WithTimeout(AzureTableDefaultPolicies.TableOperationTimeout);

            Assert.IsTrue(didInsert, "Did insert");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task SiloInstanceTable_Register_CheckData()
        {
            const string testName = "SiloInstanceTable_Register_CheckData";
            logger.Info("Start {0}", testName);

            RegisterSiloInstance();

            var data = await FindSiloEntry(siloAddress);
            SiloInstanceTableEntry siloEntry = data.Item1;
            string eTag = data.Item2;

            Assert.IsNotNull(eTag, "ETag should not be null");
            Assert.IsNotNull(siloEntry, "SiloInstanceTableEntry should not be null");

            Assert.AreEqual(SiloInstanceTableTestConstants.INSTANCE_STATUS_CREATED, siloEntry.Status);

            CheckSiloInstanceTableEntry(myEntry, siloEntry);
            logger.Info("End {0}", testName);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task SiloInstanceTable_Activate_CheckData()
        {
            RegisterSiloInstance();

            manager.ActivateSiloInstance(myEntry);

            var data = await FindSiloEntry(siloAddress);
            Assert.IsNotNull(data, "Data returned should not be null");

            SiloInstanceTableEntry siloEntry = data.Item1;
            string eTag = data.Item2;

            Assert.IsNotNull(eTag, "ETag should not be null");
            Assert.IsNotNull(siloEntry, "SiloInstanceTableEntry should not be null");

            Assert.AreEqual(SiloInstanceTableTestConstants.INSTANCE_STATUS_ACTIVE, siloEntry.Status);

            CheckSiloInstanceTableEntry(myEntry, siloEntry);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task SiloInstanceTable_Unregister_CheckData()
        {
            RegisterSiloInstance();

            manager.UnregisterSiloInstance(myEntry);

            var data = await FindSiloEntry(siloAddress);
            SiloInstanceTableEntry siloEntry = data.Item1;
            string eTag = data.Item2;

            Assert.IsNotNull(eTag, "ETag should not be null");
            Assert.IsNotNull(siloEntry, "SiloInstanceTableEntry should not be null");

            Assert.AreEqual(SiloInstanceTableTestConstants.INSTANCE_STATUS_DEAD, siloEntry.Status);

            CheckSiloInstanceTableEntry(myEntry, siloEntry);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public void SiloInstanceTable_FindAllGatewayProxyEndpoints()
        {
            RegisterSiloInstance();

            var gateways = manager.FindAllGatewayProxyEndpoints();
            Assert.AreEqual(0, gateways.Count, "Number of gateways before Silo.Activate");

            manager.ActivateSiloInstance(myEntry);

            gateways = manager.FindAllGatewayProxyEndpoints();
            Assert.AreEqual(1, gateways.Count, "Number of gateways after Silo.Activate");

            Uri myGateway = gateways.First();
            Assert.AreEqual(myEntry.Address, myGateway.Host.ToString(), "Gateway address");
            Assert.AreEqual(myEntry.ProxyPort, myGateway.Port.ToString(CultureInfo.InvariantCulture), "Gateway port");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Azure"), TestCategory("Storage")]
        public void SiloAddress_ToFrom_RowKey()
        {
            string ipAddress = "1.2.3.4";
            int port = 5555;
            int generation = 6666;

            IPAddress address = IPAddress.Parse(ipAddress);
            IPEndPoint endpoint = new IPEndPoint(address, port);
            SiloAddress siloAddress = SiloAddress.New(endpoint, generation);

            string MembershipRowKey = SiloInstanceTableEntry.ConstructRowKey(siloAddress);

            Console.WriteLine("SiloAddress = {0} Row Key string = {1}", siloAddress, MembershipRowKey);

            SiloAddress fromRowKey = SiloInstanceTableEntry.UnpackRowKey(MembershipRowKey);

            Console.WriteLine("SiloAddress result = {0} From Row Key string = {1}", fromRowKey, MembershipRowKey);

            Assert.AreEqual(siloAddress, fromRowKey, "Compare SiloAddress");
            Assert.AreEqual(SiloInstanceTableEntry.ConstructRowKey(siloAddress), SiloInstanceTableEntry.ConstructRowKey(fromRowKey), "SiloInstanceTableEntry.ConstructRowKey");
        }

        private void RegisterSiloInstance()
        {
            string partitionKey = deploymentId;
            string rowKey = SiloInstanceTableEntry.ConstructRowKey(siloAddress);

            IPEndPoint myEndpoint = siloAddress.Endpoint;

            myEntry = new SiloInstanceTableEntry
            {
                PartitionKey = partitionKey,
                RowKey = rowKey,

                DeploymentId = deploymentId,
                Address = myEndpoint.Address.ToString(),
                Port = myEndpoint.Port.ToString(CultureInfo.InvariantCulture),
                Generation = generation.ToString(CultureInfo.InvariantCulture),

                HostName = myEndpoint.Address.ToString(),
                ProxyPort = "30000",

                RoleName = "MyRole",
                InstanceName = "MyInstance",
                UpdateZone = "0",
                FaultZone = "0",
                StartTime = TraceLogger.PrintDate(DateTime.UtcNow),
            };

            logger.Info("MyEntry={0}", myEntry);

            manager.RegisterSiloInstance(myEntry);
        }

        private async Task<Tuple<SiloInstanceTableEntry, string>> FindSiloEntry(SiloAddress siloAddr)
        {
            string partitionKey = deploymentId;
            string rowKey = SiloInstanceTableEntry.ConstructRowKey(siloAddr);

            logger.Info("FindSiloEntry for SiloAddress={0} PartitionKey={1} RowKey={2}", siloAddr, partitionKey, rowKey);

            Tuple<SiloInstanceTableEntry, string> data = await manager.ReadSingleTableEntryAsync(partitionKey, rowKey);

            logger.Info("FindSiloEntry returning Data={0}", data);
            return data;
        }

        private void CheckSiloInstanceTableEntry(SiloInstanceTableEntry referenceEntry, SiloInstanceTableEntry entry)
        {
            Assert.AreEqual(referenceEntry.DeploymentId, entry.DeploymentId, "DeploymentId");
            Assert.AreEqual(referenceEntry.Address, entry.Address, "Address");
            Assert.AreEqual(referenceEntry.Port, entry.Port, "Port");
            Assert.AreEqual(referenceEntry.Generation, entry.Generation, "Generation");
            Assert.AreEqual(referenceEntry.HostName, entry.HostName, "HostName");
            //Assert.AreEqual(referenceEntry.Status, entry.Status, "Status");
            Assert.AreEqual(referenceEntry.ProxyPort, entry.ProxyPort, "ProxyPort");
            Assert.AreEqual(referenceEntry.RoleName, entry.RoleName, "RoleName");
            Assert.AreEqual(referenceEntry.InstanceName, entry.InstanceName, "InstanceName");
            Assert.AreEqual(referenceEntry.UpdateZone, entry.UpdateZone, "UpdateZone");
            Assert.AreEqual(referenceEntry.FaultZone, entry.FaultZone, "FaultZone");
            Assert.AreEqual(referenceEntry.StartTime, entry.StartTime, "StartTime");
            Assert.AreEqual(referenceEntry.IAmAliveTime, entry.IAmAliveTime, "IAmAliveTime");
            Assert.AreEqual(referenceEntry.MembershipVersion, entry.MembershipVersion, "MembershipVersion");

            Assert.AreEqual(referenceEntry.SuspectingTimes, entry.SuspectingTimes, "SuspectingTimes");
            Assert.AreEqual(referenceEntry.SuspectingSilos, entry.SuspectingSilos, "SuspectingSilos");
        }
    }
}
