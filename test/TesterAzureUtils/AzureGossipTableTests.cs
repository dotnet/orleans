using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Orleans.MultiCluster;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MultiClusterNetwork;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils
{
    public class AzureGossipTableTests : AzureStorageBasicTests 
    {
        private readonly Logger logger;

        private Guid globalServiceId; //this should be the same for all clusters. Use this as partition key.
        //this should be unique per cluster. Can we use deployment id? 
        //problem with only using deployment id is that it is not known before deployment and hence not in the config file.
        private string deploymentId;
        private SiloAddress siloAddress1;
        private SiloAddress siloAddress2;
        private static readonly TimeSpan timeout = TimeSpan.FromMinutes(1);
        private AzureTableBasedGossipChannel gossipTable; // This type is internal

        public AzureGossipTableTests()
        {
            logger = LogManager.GetLogger("AzureGossipTableTests", LoggerType.Application);
        
            globalServiceId = Guid.NewGuid();
            deploymentId = "test-" + globalServiceId;

            IPAddress ip;
            if (!IPAddress.TryParse("127.0.0.1", out ip))
            {
                logger.Error(-1, "Could not parse ip address");
                return;
            }
            IPEndPoint ep1 = new IPEndPoint(ip, 21111);
            siloAddress1 = SiloAddress.New(ep1, 0);
            IPEndPoint ep2 = new IPEndPoint(ip, 21112);
            siloAddress2 = SiloAddress.New(ep2, 0);

            logger.Info("DeploymentId={0}", deploymentId);

            GlobalConfiguration config = new GlobalConfiguration
            {
                ServiceId = globalServiceId,
                ClusterId = "0",
                DeploymentId = deploymentId,
                DataConnectionString = TestDefaultConfiguration.DataConnectionString
            };

            gossipTable = new AzureTableBasedGossipChannel();
            var done = gossipTable.Initialize(config.ServiceId, config.DataConnectionString);
            if (!done.Wait(timeout))
            {
                throw new TimeoutException("Could not create/read table.");
            }
        }

        [SkippableFact, TestCategory("Functional"), TestCategory("GeoCluster"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureGossip_ConfigGossip()
        {
            // start clean
            await gossipTable.DeleteAllEntries();

            // push empty data
            await gossipTable.Publish(new MultiClusterData());

            // push and pull empty data
            var answer = await gossipTable.Synchronize(new MultiClusterData());
            Assert.True(answer.IsEmpty);

            var ts1 = new DateTime(year: 2011, month: 1, day: 1);
            var ts2 = new DateTime(year: 2012, month: 2, day: 2);
            var ts3 = new DateTime(year: 2013, month: 3, day: 3);

            var conf1 = new MultiClusterConfiguration(ts1, new string[] { "A" }.ToList(), "comment");
            var conf2 = new MultiClusterConfiguration(ts2, new string[] { "A", "B", "C" }.ToList());
            var conf3 = new MultiClusterConfiguration(ts3, new string[] { }.ToList());

            // push configuration 1
            await gossipTable.Publish(new MultiClusterData(conf1));

            // retrieve (by push/pull empty)
            answer = await gossipTable.Synchronize(new MultiClusterData());
            Assert.Equal(conf1, answer.Configuration);

            // gossip stable
            answer = await gossipTable.Synchronize(new MultiClusterData(conf1));
            Assert.True(answer.IsEmpty);

            // push configuration 2
            answer = await gossipTable.Synchronize(new MultiClusterData(conf2));
            Assert.True(answer.IsEmpty);

            // gossip returns latest
            answer = await gossipTable.Synchronize(new MultiClusterData(conf1));
            Assert.Equal(conf2, answer.Configuration);
            await gossipTable.Publish(new MultiClusterData(conf1));
            answer = await gossipTable.Synchronize(new MultiClusterData());
            Assert.Equal(conf2, answer.Configuration);
            answer = await gossipTable.Synchronize(new MultiClusterData(conf2));
            Assert.True(answer.IsEmpty);

            // push final configuration
            answer = await gossipTable.Synchronize(new MultiClusterData(conf3));
            Assert.True(answer.IsEmpty);

            answer = await gossipTable.Synchronize(new MultiClusterData(conf1));
            Assert.Equal(conf3, answer.Configuration);
        }

        [SkippableFact, TestCategory("Functional"), TestCategory("GeoCluster"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureGossip_GatewayGossip()
        {
            // start clean
            await gossipTable.DeleteAllEntries();

            var ts1 = DateTime.UtcNow;
            var ts2 = ts1 + new TimeSpan(hours: 0, minutes: 0, seconds: 1);
            var ts3 = ts1 + new TimeSpan(hours: 0, minutes: 0, seconds: 2);

            var G1 = new GatewayEntry()
            {
                SiloAddress = siloAddress1,
                ClusterId = "1",
                HeartbeatTimestamp = ts1,
                Status = GatewayStatus.Active
            };
            var G2 = new GatewayEntry()
            {
                SiloAddress = siloAddress1,
                ClusterId = "1",
                HeartbeatTimestamp = ts3,
                Status = GatewayStatus.Inactive
            };
            var H1 = new GatewayEntry()
            {
                SiloAddress = siloAddress2,
                ClusterId = "2",
                HeartbeatTimestamp = ts2,
                Status = GatewayStatus.Active
            };
            var H2 = new GatewayEntry()
            {
                SiloAddress = siloAddress2,
                ClusterId = "2",
                HeartbeatTimestamp = ts3,
                Status = GatewayStatus.Inactive
            };

            // push G1
            await gossipTable.Publish(new MultiClusterData(G1));

            // push H1, retrieve G1 
            var answer = await gossipTable.Synchronize(new MultiClusterData(H1));
            Assert.Equal(1, answer.Gateways.Count);
            Assert.True(answer.Gateways.ContainsKey(siloAddress1));
            Assert.Equal(G1, answer.Gateways[siloAddress1]);

            // push G2, retrieve H1
            answer = await gossipTable.Synchronize(new MultiClusterData(G2));
            Assert.Equal(1, answer.Gateways.Count);
            Assert.True(answer.Gateways.ContainsKey(siloAddress2));
            Assert.Equal(H1, answer.Gateways[siloAddress2]);

            // gossip stable
            await gossipTable.Publish(new MultiClusterData(H1));
            await gossipTable.Publish(new MultiClusterData(G1));
            answer = await gossipTable.Synchronize(new MultiClusterData(new GatewayEntry[] { H1, G2 }));
            Assert.True(answer.IsEmpty);

            // retrieve
            answer = await gossipTable.Synchronize(new MultiClusterData(new GatewayEntry[] { H1, G2 }));
            Assert.True(answer.IsEmpty);

            // push H2 
            await gossipTable.Publish(new MultiClusterData(H2));

            // retrieve all
            answer = await gossipTable.Synchronize(new MultiClusterData(new GatewayEntry[] { G1, H1 }));
            Assert.Equal(2, answer.Gateways.Count);
            Assert.True(answer.Gateways.ContainsKey(siloAddress1));
            Assert.True(answer.Gateways.ContainsKey(siloAddress2));
            Assert.Equal(G2, answer.Gateways[siloAddress1]);
            Assert.Equal(H2, answer.Gateways[siloAddress2]);
        }
         
    }
}
