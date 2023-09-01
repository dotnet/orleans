using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.TestingHost.Utils;
using TestExtensions;
using UnitTests.MembershipTests;
using Xunit;
using Xunit.Abstractions;
using Orleans.Internal;
using Orleans.Clustering.AzureStorage;

namespace Tester.AzureUtils
{
    /// <summary>
    /// Tests for operation of Orleans SiloInstanceManager using AzureStore - Requires access to external Azure storage
    /// </summary>
    [TestCategory("AzureStorage"), TestCategory("Storage")]
    public class SiloInstanceTableManagerTests : IClassFixture<SiloInstanceTableManagerTests.Fixture>, IDisposable
    {
        public class Fixture : IDisposable
        {
            public ILoggerFactory LoggerFactory { get; set; } =
                TestingUtils.CreateDefaultLoggerFactory("SiloInstanceTableManagerTests.log");

            public void Dispose()
            {
                this.LoggerFactory.Dispose();
            }
        }

        private readonly string clusterId;
        private int generation;
        private SiloAddress siloAddress;
        private SiloInstanceTableEntry myEntry;
        private OrleansSiloInstanceManager manager;
        private readonly ITestOutputHelper output;

        public SiloInstanceTableManagerTests(ITestOutputHelper output, Fixture fixture)
        {
            TestUtils.CheckForAzureStorage();
            this.output = output;
            this.clusterId = "test-" + Guid.NewGuid();
            generation = SiloAddress.AllocateNewGeneration();
            siloAddress = SiloAddressUtils.NewLocalSiloAddress(generation);

            output.WriteLine("ClusterId={0} Generation={1}", this.clusterId, generation);

            output.WriteLine("Initializing SiloInstanceManager");
            manager = OrleansSiloInstanceManager.GetManager(
                this.clusterId,
                fixture.LoggerFactory,
                new AzureStorageClusteringOptions { TableName = new AzureStorageClusteringOptions().TableName }.ConfigureTestDefaults())
                .WaitForResultWithThrow(SiloInstanceTableTestConstants.Timeout);
        }

        // Use TestCleanup to run code after each test has run
        public void Dispose()
        {
            if(manager != null && SiloInstanceTableTestConstants.DeleteEntriesAfterTest)
            {
                TimeSpan timeout = SiloInstanceTableTestConstants.Timeout;

                output.WriteLine("TestCleanup Timeout={0}", timeout);

                manager.DeleteTableEntries(this.clusterId).WaitWithThrow(timeout);

                output.WriteLine("TestCleanup -  Finished");
                manager = null;
            }
        }

        [SkippableFact, TestCategory("Functional")]
        public void SiloInstanceTable_Op_RegisterSiloInstance()
        {
            RegisterSiloInstance();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task SiloInstanceTable_Op_ActivateSiloInstance()
        {
            RegisterSiloInstance();

            await manager.ActivateSiloInstance(myEntry);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task SiloInstanceTable_Op_UnregisterSiloInstance()
        {
            RegisterSiloInstance();

            await manager.UnregisterSiloInstance(myEntry);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task SiloInstanceTable_Op_CleanDeadSiloInstance()
        {
            // Register a silo entry
            await manager.TryCreateTableVersionEntryAsync();
            this.generation = 0;
            RegisterSiloInstance();
            // and mark it as dead
            await manager.UnregisterSiloInstance(myEntry);

            // Create new active entries
            for (int i = 1; i < 5; i++)
            {
                this.generation = i;
                this.siloAddress = SiloAddressUtils.NewLocalSiloAddress(generation);
                var instance = RegisterSiloInstance();
                await manager.ActivateSiloInstance(instance);
            }

            await Task.Delay(TimeSpan.FromSeconds(3));

            await manager.CleanupDefunctSiloEntries(DateTime.Now - TimeSpan.FromSeconds(1));

            var entries = await manager.FindAllSiloEntries();
            Assert.Equal(5, entries.Count);
            Assert.All(entries, e => Assert.NotEqual(SiloInstanceTableTestConstants.INSTANCE_STATUS_DEAD, e.Item1.Status));
        }


        [SkippableFact, TestCategory("Functional")]
        public async Task SiloInstanceTable_Op_CreateSiloEntryConditionally()
        {
            bool didInsert = await manager.TryCreateTableVersionEntryAsync()
                .WithTimeout(new AzureStoragePolicyOptions().OperationTimeout);

            Assert.True(didInsert, "Did insert");
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task SiloInstanceTable_Register_CheckData()
        {
            const string testName = "SiloInstanceTable_Register_CheckData";
            output.WriteLine("Start {0}", testName);

            RegisterSiloInstance();

            var data = await FindSiloEntry(siloAddress);
            SiloInstanceTableEntry siloEntry = data.Entity;
            string eTag = data.ETag;

            Assert.NotNull(eTag); // ETag should not be null
            Assert.NotNull(siloEntry); // SiloInstanceTableEntry should not be null

            Assert.Equal(SiloInstanceTableTestConstants.INSTANCE_STATUS_CREATED, siloEntry.Status);

            CheckSiloInstanceTableEntry(myEntry, siloEntry);
            output.WriteLine("End {0}", testName);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task SiloInstanceTable_Activate_CheckData()
        {
            RegisterSiloInstance();

            await manager.ActivateSiloInstance(myEntry);

            var data = await FindSiloEntry(siloAddress);
            Assert.NotNull(data.Entity); // Data returned should not be null

            SiloInstanceTableEntry siloEntry = data.Entity;
            string eTag = data.ETag;

            Assert.NotNull(eTag); // ETag should not be null
            Assert.NotNull(siloEntry); // SiloInstanceTableEntry should not be null

            Assert.Equal(SiloInstanceTableTestConstants.INSTANCE_STATUS_ACTIVE, siloEntry.Status);

            CheckSiloInstanceTableEntry(myEntry, siloEntry);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task SiloInstanceTable_Unregister_CheckData()
        {
            RegisterSiloInstance();

            await manager.UnregisterSiloInstance(myEntry);

            var data = await FindSiloEntry(siloAddress);
            SiloInstanceTableEntry siloEntry = data.Entity;
            string eTag = data.ETag;

            Assert.NotNull(eTag); // ETag should not be null
            Assert.NotNull(siloEntry); // SiloInstanceTableEntry should not be null

            Assert.Equal(SiloInstanceTableTestConstants.INSTANCE_STATUS_DEAD, siloEntry.Status);

            CheckSiloInstanceTableEntry(myEntry, siloEntry);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task SiloInstanceTable_FindAllGatewayProxyEndpoints()
        {
            RegisterSiloInstance();

            var gateways = await manager.FindAllGatewayProxyEndpoints();
            Assert.Equal(0,  gateways.Count);  // "Number of gateways before Silo.Activate"

            await manager.ActivateSiloInstance(myEntry);

            gateways = await manager.FindAllGatewayProxyEndpoints();
            Assert.Equal(1,  gateways.Count);  // "Number of gateways after Silo.Activate"

            Uri myGateway = gateways.First();
            Assert.Equal(myEntry.Address,  myGateway.Host.ToString());  // "Gateway address"
            Assert.Equal(myEntry.ProxyPort,  myGateway.Port.ToString(CultureInfo.InvariantCulture));  // "Gateway port"
        }

        [SkippableFact, TestCategory("Functional")]
        public void SiloAddress_ToFrom_RowKey()
        {
            string ipAddress = "1.2.3.4";
            int port = 5555;
            int generation = 6666;

            IPAddress address = IPAddress.Parse(ipAddress);
            IPEndPoint endpoint = new IPEndPoint(address, port);
            SiloAddress siloAddress = SiloAddress.New(endpoint, generation);

            string MembershipRowKey = SiloInstanceTableEntry.ConstructRowKey(siloAddress);

            output.WriteLine("SiloAddress = {0} Row Key string = {1}", siloAddress, MembershipRowKey);

            SiloAddress fromRowKey = SiloInstanceTableEntry.UnpackRowKey(MembershipRowKey);

            output.WriteLine("SiloAddress result = {0} From Row Key string = {1}", fromRowKey, MembershipRowKey);

            Assert.Equal(siloAddress,  fromRowKey);
            Assert.Equal(SiloInstanceTableEntry.ConstructRowKey(siloAddress), SiloInstanceTableEntry.ConstructRowKey(fromRowKey));
        }

        private SiloInstanceTableEntry RegisterSiloInstance()
        {
            string partitionKey = this.clusterId;
            string rowKey = SiloInstanceTableEntry.ConstructRowKey(siloAddress);

            IPEndPoint myEndpoint = siloAddress.Endpoint;

            myEntry = new SiloInstanceTableEntry
            {
                PartitionKey = partitionKey,
                RowKey = rowKey,

                DeploymentId = this.clusterId,
                Address = myEndpoint.Address.ToString(),
                Port = myEndpoint.Port.ToString(CultureInfo.InvariantCulture),
                Generation = generation.ToString(CultureInfo.InvariantCulture),

                HostName = myEndpoint.Address.ToString(),
                ProxyPort = "30000",

                RoleName = "MyRole",
                SiloName = "MyInstance",
                UpdateZone = "0",
                FaultZone = "0",
                StartTime = LogFormatter.PrintDate(DateTime.UtcNow),
            };

            output.WriteLine("MyEntry={0}", myEntry);

            manager.RegisterSiloInstance(myEntry);
            return myEntry;
        }

        private async Task<(SiloInstanceTableEntry Entity, string ETag)> FindSiloEntry(SiloAddress siloAddr)
        {
            string partitionKey = this.clusterId;
            string rowKey = SiloInstanceTableEntry.ConstructRowKey(siloAddr);

            output.WriteLine("FindSiloEntry for SiloAddress={0} PartitionKey={1} RowKey={2}", siloAddr, partitionKey, rowKey);

            var data = await manager.ReadSingleTableEntryAsync(partitionKey, rowKey);

            output.WriteLine("FindSiloEntry returning Data={0}", data);
            return data;
        }

        private void CheckSiloInstanceTableEntry(SiloInstanceTableEntry referenceEntry, SiloInstanceTableEntry entry)
        {
            Assert.Equal(referenceEntry.DeploymentId, entry.DeploymentId);
            Assert.Equal(referenceEntry.Address, entry.Address);
            Assert.Equal(referenceEntry.Port, entry.Port);
            Assert.Equal(referenceEntry.Generation,  entry.Generation);
            Assert.Equal(referenceEntry.HostName, entry.HostName);
            //Assert.Equal(referenceEntry.Status, entry.Status);
            Assert.Equal(referenceEntry.ProxyPort, entry.ProxyPort);
            Assert.Equal(referenceEntry.RoleName, entry.RoleName);
            Assert.Equal(referenceEntry.SiloName, entry.SiloName);
            Assert.Equal(referenceEntry.UpdateZone, entry.UpdateZone);
            Assert.Equal(referenceEntry.FaultZone, entry.FaultZone);
            Assert.Equal(referenceEntry.StartTime, entry.StartTime);
            Assert.Equal(referenceEntry.IAmAliveTime, entry.IAmAliveTime);
            Assert.Equal(referenceEntry.MembershipVersion, entry.MembershipVersion);

            Assert.Equal(referenceEntry.SuspectingTimes, entry.SuspectingTimes);
            Assert.Equal(referenceEntry.SuspectingSilos, entry.SuspectingSilos);
        }
    }
}
