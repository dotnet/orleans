//#define USE_SQL_SERVER

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using UnitTests.Tester;
using UnitTests.TestHelper;

namespace UnitTests.MessageCenterTests
{
    [TestClass]
    public class GatewaySelectionTest
    {
        public TestContext TestContext { get; set; }

        private static readonly List<Uri> gatewayAddressUris = new[]
        {
            new Uri("gwy.tcp://127.0.0.1:1/0"),
            new Uri("gwy.tcp://127.0.0.1:2/0"),
            new Uri("gwy.tcp://127.0.0.1:3/0"),
            new Uri("gwy.tcp://127.0.0.1:4/0")
        }.ToList();

        [TestInitialize]
        public void TestInitialize()
        {
            GrainClient.Uninitialize();
            GrainClient.TestOnlyNoConnect = false;
        }

        [TestCleanup]
        public void TestCleanup()
        {
            GrainClient.Uninitialize();
            GrainClient.TestOnlyNoConnect = false;
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Gateway")]
        public void GatewaySelection()
        {
            var listProvider = new TestListProvider(gatewayAddressUris);
            Test_GatewaySelection(listProvider);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Gateway")]
        public void GatewaySelection_ClientInit_EmptyList()
        {
            var cfg = new ClientConfiguration();
            cfg.Gateways = null;
            bool failed = false;
            try
            {
                GrainClient.Initialize(cfg);
            }
            catch (Exception exc)
            {
                Console.WriteLine(exc);
                failed = true;
            }
            Assert.IsTrue(failed, "GatewaySelection_EmptyList failed as GatewayManager did not throw on empty Gateway list.");

            // Note: This part of the test case requires a silo local to be running in order to work successfully.

            //var listProvider = new TestListProvider(gatewayAddressUris);
            //cfg.Gateways = listProvider.GetGateways().Select(uri =>
            //{
            //    return new IPEndPoint(IPAddress.Parse(uri.Host), uri.Port);
            //}).ToList();
            //Client.Initialize(cfg);
        }

#if USE_SQL_SERVER || DEBUG
        [TestMethod, TestCategory("Gateway"), TestCategory("SqlServer")]
        public async Task GatewaySelection_SqlServer()
        {
            string testName = TestContext.TestName;

            Console.WriteLine(UnitTestSiloHost.DumpTestContext(TestContext));

            Guid serviceId = Guid.NewGuid();

            GlobalConfiguration cfg = new GlobalConfiguration
            {
                ServiceId = serviceId,
                DeploymentId = testName,
                DataConnectionString = TestUtils.GetSqlConnectionString(TestContext)
            };

            var membership = new SqlMembershipTable();
            var logger = TraceLogger.GetLogger(membership.GetType().Name);
            await membership.InitializeMembershipTable(cfg, true, logger);

            IMembershipTable membershipTable = membership;

            // Pre-populate gateway table with data
            int count = 1;
            foreach (Uri gateway in gatewayAddressUris)
            {
                Console.WriteLine("Adding gataway data for {0}", gateway);

                SiloAddress siloAddress = gateway.ToSiloAddress();
                Assert.IsNotNull(siloAddress, "Unable to get SiloAddress from Uri {0}", gateway);

                MembershipEntry MembershipEntry = new MembershipEntry
                {
                    SiloAddress = siloAddress,
                    HostName = gateway.Host,
                    Status = SiloStatus.Active,
                    ProxyPort = gateway.Port,
                    StartTime = DateTime.UtcNow
                };

                var tableVersion = new TableVersion(count, Guid.NewGuid().ToString());

                Console.WriteLine("Inserting gataway data for {0} with TableVersion={1}", MembershipEntry, tableVersion);

                bool ok = await membershipTable.InsertRow(MembershipEntry, tableVersion);
                count++;
                Assert.IsTrue(ok, "Membership record should have been written OK but were not: {0}", MembershipEntry);

                Console.WriteLine("Successfully inserted Membership row {0}", MembershipEntry);
            }

            MembershipTableData data = await membershipTable.ReadAll();
            Assert.IsNotNull(data, "MembershipTableData returned");
            Assert.AreEqual(gatewayAddressUris.Count, data.Members.Count, "Number of gateway records read");

            IGatewayListProvider listProvider = membership;

            Test_GatewaySelection(listProvider);
        }
#endif

        private void Test_GatewaySelection(IGatewayListProvider listProvider)
        {
            IList<Uri> gatewayUris = listProvider.GetGateways().GetResult();
            Assert.IsTrue(gatewayUris.Count > 0, "Found some gateways. Data = {0}", Utils.EnumerableToString(gatewayUris));

            var gatewayEndpoints = gatewayUris.Select(uri =>
            {
                return new IPEndPoint(IPAddress.Parse(uri.Host), uri.Port);
            }).ToList();

            var cfg = new ClientConfiguration
            {
                Gateways = gatewayEndpoints
            };
            var gatewayManager = new GatewayManager(cfg, listProvider);

            var counts = new int[4];

            for (int i = 0; i < 2300; i++)
            {
                var ip = gatewayManager.GetLiveGateway();
                var addr = IPAddress.Parse(ip.Host);
                Assert.AreEqual(IPAddress.Loopback, addr, "Incorrect IP address returned for gateway");
                Assert.IsTrue((0 < ip.Port) && (ip.Port < 5), "Incorrect IP port returned for gateway");
                counts[ip.Port - 1]++;
            }

            // The following needed to be changed as the gateway manager now round-robins through the available gateways, rather than
            // selecting randomly based on load numbers.
            //Assert.IsTrue((500 < counts[0]) && (counts[0] < 1500), "Gateway selection is incorrectly skewed");
            //Assert.IsTrue((500 < counts[1]) && (counts[1] < 1500), "Gateway selection is incorrectly skewed");
            //Assert.IsTrue((125 < counts[2]) && (counts[2] < 375), "Gateway selection is incorrectly skewed");
            //Assert.IsTrue((25 < counts[3]) && (counts[3] < 75), "Gateway selection is incorrectly skewed");
            //Assert.IsTrue((287 < counts[0]) && (counts[0] < 1150), "Gateway selection is incorrectly skewed");
            //Assert.IsTrue((287 < counts[1]) && (counts[1] < 1150), "Gateway selection is incorrectly skewed");
            //Assert.IsTrue((287 < counts[2]) && (counts[2] < 1150), "Gateway selection is incorrectly skewed");
            //Assert.IsTrue((287 < counts[3]) && (counts[3] < 1150), "Gateway selection is incorrectly skewed");

            int low = 2300 / 4;
            int up = 2300 / 4;
            Assert.IsTrue((low <= counts[0]) && (counts[0] <= up), "Gateway selection is incorrectly skewed. " + counts[0]);
            Assert.IsTrue((low <= counts[1]) && (counts[1] <= up), "Gateway selection is incorrectly skewed. " + counts[1]);
            Assert.IsTrue((low <= counts[2]) && (counts[2] <= up), "Gateway selection is incorrectly skewed. " + counts[2]);
            Assert.IsTrue((low <= counts[3]) && (counts[3] <= up), "Gateway selection is incorrectly skewed. " + counts[3]);
        }

        private class TestListProvider : IGatewayListProvider
        {
            private readonly IList<Uri> list;

            public TestListProvider(List<Uri> gatewayUris)
            {
                list = gatewayUris;
            }

            #region Implementation of IGatewayListProvider

            public Task<IList<Uri>> GetGateways()
            {
                return Task.FromResult(list);
            }

            public TimeSpan MaxStaleness
            {
                get { return TimeSpan.FromMinutes(1); }
            }

            public bool IsUpdatable
            {
                get { return false; }
            }
            public Task InitializeGatewayListProvider(ClientConfiguration clientConfiguration, TraceLogger traceLogger)
            {
                return TaskDone.Done;
            }

            #endregion


        }
    }
}
