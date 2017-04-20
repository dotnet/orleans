using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using TestExtensions;
using UnitTests.MembershipTests;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AzureUtils
{
    /// <summary>
    /// Tests for operation of Orleans SiloInstanceManager using AzureStore - Requires access to external Azure storage
    /// </summary>
    [TestCategory("Azure"), TestCategory("Storage")]
    public class SiloInstanceTableManagerTests : IClassFixture<SiloInstanceTableManagerTests.Fixture>, IDisposable
    {
        public class Fixture
        {
            public Fixture()
            {
                LogManager.Initialize(new NodeConfiguration());
            }
        }

        private string deploymentId;
        private int generation;
        private SiloAddress siloAddress;
        private SiloInstanceTableEntry myEntry;
        private OrleansSiloInstanceManager manager;
        private readonly Logger logger;
        private readonly ITestOutputHelper output;

        public SiloInstanceTableManagerTests(ITestOutputHelper output)
        {
            TestUtils.CheckForAzureStorage();
            this.output = output;
            logger = LogManager.GetLogger("SiloInstanceTableManagerTests", LoggerType.Application);

            deploymentId = "test-" + Guid.NewGuid();
            generation = SiloAddress.AllocateNewGeneration();
            siloAddress = SiloAddress.NewLocalAddress(generation);

            logger.Info("DeploymentId={0} Generation={1}", deploymentId, generation);

            logger.Info("Initializing SiloInstanceManager");
            manager = OrleansSiloInstanceManager.GetManager(deploymentId, TestDefaultConfiguration.DataConnectionString)
                .WaitForResultWithThrow(SiloInstanceTableTestConstants.Timeout);
        }

        // Use TestCleanup to run code after each test has run
        public void Dispose()
        {
            if(manager != null && SiloInstanceTableTestConstants.DeleteEntriesAfterTest)
            {
                TimeSpan timeout = SiloInstanceTableTestConstants.Timeout;

                logger.Info("TestCleanup Timeout={0}", timeout);

                manager.DeleteTableEntries(deploymentId).WaitWithThrow(timeout);

                logger.Info("TestCleanup -  Finished");
                manager = null;
            }
        }

        [SkippableFact, TestCategory("Functional")]
        public void SiloInstanceTable_Op_RegisterSiloInstance()
        {
            RegisterSiloInstance();
        }

        [SkippableFact, TestCategory("Functional")]
        public void SiloInstanceTable_Op_ActivateSiloInstance()
        {
            RegisterSiloInstance();

            manager.ActivateSiloInstance(myEntry);
        }

        [SkippableFact, TestCategory("Functional")]
        public void SiloInstanceTable_Op_UnregisterSiloInstance()
        {
            RegisterSiloInstance();

            manager.UnregisterSiloInstance(myEntry);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task SiloInstanceTable_Op_CreateSiloEntryConditionally()
        {
            bool didInsert = await manager.TryCreateTableVersionEntryAsync()
                .WithTimeout(AzureTableDefaultPolicies.TableOperationTimeout);

            Assert.True(didInsert, "Did insert");
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task SiloInstanceTable_Register_CheckData()
        {
            const string testName = "SiloInstanceTable_Register_CheckData";
            logger.Info("Start {0}", testName);

            RegisterSiloInstance();

            var data = await FindSiloEntry(siloAddress);
            SiloInstanceTableEntry siloEntry = data.Item1;
            string eTag = data.Item2;

            Assert.NotNull(eTag); // ETag should not be null
            Assert.NotNull(siloEntry); // SiloInstanceTableEntry should not be null

            Assert.Equal(SiloInstanceTableTestConstants.INSTANCE_STATUS_CREATED, siloEntry.Status);

            CheckSiloInstanceTableEntry(myEntry, siloEntry);
            logger.Info("End {0}", testName);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task SiloInstanceTable_Activate_CheckData()
        {
            RegisterSiloInstance();

            manager.ActivateSiloInstance(myEntry);

            var data = await FindSiloEntry(siloAddress);
            Assert.NotNull(data); // Data returned should not be null

            SiloInstanceTableEntry siloEntry = data.Item1;
            string eTag = data.Item2;

            Assert.NotNull(eTag); // ETag should not be null
            Assert.NotNull(siloEntry); // SiloInstanceTableEntry should not be null

            Assert.Equal(SiloInstanceTableTestConstants.INSTANCE_STATUS_ACTIVE, siloEntry.Status);

            CheckSiloInstanceTableEntry(myEntry, siloEntry);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task SiloInstanceTable_Unregister_CheckData()
        {
            RegisterSiloInstance();

            manager.UnregisterSiloInstance(myEntry);

            var data = await FindSiloEntry(siloAddress);
            SiloInstanceTableEntry siloEntry = data.Item1;
            string eTag = data.Item2;

            Assert.NotNull(eTag); // ETag should not be null
            Assert.NotNull(siloEntry); // SiloInstanceTableEntry should not be null

            Assert.Equal(SiloInstanceTableTestConstants.INSTANCE_STATUS_DEAD, siloEntry.Status);

            CheckSiloInstanceTableEntry(myEntry, siloEntry);
        }

        [SkippableFact, TestCategory("Functional")]
        public void SiloInstanceTable_FindAllGatewayProxyEndpoints()
        {
            RegisterSiloInstance();

            var gateways = manager.FindAllGatewayProxyEndpoints().GetResult();
            Assert.Equal(0,  gateways.Count);  // "Number of gateways before Silo.Activate"

            manager.ActivateSiloInstance(myEntry);

            gateways = manager.FindAllGatewayProxyEndpoints().GetResult();
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
                SiloName = "MyInstance",
                UpdateZone = "0",
                FaultZone = "0",
                StartTime = LogFormatter.PrintDate(DateTime.UtcNow),
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
