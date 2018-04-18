using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.MultiCluster;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MultiClusterNetwork;
using Orleans.TestingHost.Utils;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils
{
    public class AzureGossipTableTests : AzureStorageBasicTests , IDisposable
    {
        private readonly ILogger logger;

        private Guid globalServiceId; //this should be the same for all clusters. Use this as partition key.
        private SiloAddress siloAddress1;
        private SiloAddress siloAddress2;
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);
        private AzureTableBasedGossipChannel gossipTable; // This type is internal
        private readonly ILoggerFactory loggerFactory;
        public AzureGossipTableTests()
        {
            this.loggerFactory = TestingUtils.CreateDefaultLoggerFactory($"{this.GetType().Name}.log");
            this.logger = this.loggerFactory.CreateLogger<AzureGossipTableTests>();

            this.globalServiceId = Guid.NewGuid();

            IPAddress ip;
            if (!IPAddress.TryParse("127.0.0.1", out ip))
            {
                this.logger.Error(-1, "Could not parse ip address");
                return;
            }
            IPEndPoint ep1 = new IPEndPoint(ip, 21111);
            this.siloAddress1 = SiloAddress.New(ep1, 0);
            IPEndPoint ep2 = new IPEndPoint(ip, 21112);
            this.siloAddress2 = SiloAddress.New(ep2, 0);

            this.logger.Info("Global ServiceId={0}", this.globalServiceId);

            GlobalConfiguration config = new GlobalConfiguration
            {
                ServiceId = globalServiceId,
                ClusterId = "0",
                DataConnectionString = TestDefaultConfiguration.DataConnectionString
            };

            this.gossipTable = new AzureTableBasedGossipChannel(this.loggerFactory);
            var done = this.gossipTable.Initialize(config.ServiceId.ToString(), config.DataConnectionString);
            if (!done.Wait(Timeout))
            {
                throw new TimeoutException("Could not create/read table.");
            }
        }

        public void Dispose()
        {
            this.loggerFactory.Dispose();
        }

        [SkippableFact, TestCategory("Functional"), TestCategory("GeoCluster"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureGossip_ConfigGossip()
        {
            // start clean
            await this.gossipTable.DeleteAllEntries();

            // push empty data
            await this.gossipTable.Publish(new MultiClusterData());

            // push and pull empty data
            var answer = await this.gossipTable.Synchronize(new MultiClusterData());
            Assert.True(answer.IsEmpty);

            var ts1 = new DateTime(year: 2011, month: 1, day: 1);
            var ts2 = new DateTime(year: 2012, month: 2, day: 2);
            var ts3 = new DateTime(year: 2013, month: 3, day: 3);

            var conf1 = new MultiClusterConfiguration(ts1, new string[] { "A" }.ToList(), "comment");
            var conf2 = new MultiClusterConfiguration(ts2, new string[] { "A", "B", "C" }.ToList());
            var conf3 = new MultiClusterConfiguration(ts3, new string[] { }.ToList());

            // push configuration 1
            await this.gossipTable.Publish(new MultiClusterData(conf1));

            // retrieve (by push/pull empty)
            answer = await this.gossipTable.Synchronize(new MultiClusterData());
            Assert.Equal(conf1, answer.Configuration);

            // gossip stable
            answer = await this.gossipTable.Synchronize(new MultiClusterData(conf1));
            Assert.True(answer.IsEmpty);

            // push configuration 2
            answer = await this.gossipTable.Synchronize(new MultiClusterData(conf2));
            Assert.True(answer.IsEmpty);

            // gossip returns latest
            answer = await this.gossipTable.Synchronize(new MultiClusterData(conf1));
            Assert.Equal(conf2, answer.Configuration);
            await this.gossipTable.Publish(new MultiClusterData(conf1));
            answer = await this.gossipTable.Synchronize(new MultiClusterData());
            Assert.Equal(conf2, answer.Configuration);
            answer = await this.gossipTable.Synchronize(new MultiClusterData(conf2));
            Assert.True(answer.IsEmpty);

            // push final configuration
            answer = await this.gossipTable.Synchronize(new MultiClusterData(conf3));
            Assert.True(answer.IsEmpty);

            answer = await this.gossipTable.Synchronize(new MultiClusterData(conf1));
            Assert.Equal(conf3, answer.Configuration);
        }

        [SkippableFact, TestCategory("Functional"), TestCategory("GeoCluster"), TestCategory("Azure"), TestCategory("Storage")]
        public async Task AzureGossip_GatewayGossip()
        {
            // start clean
            await this.gossipTable.DeleteAllEntries();

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
            await this.gossipTable.Publish(new MultiClusterData(G1));

            // push H1, retrieve G1 
            var answer = await this.gossipTable.Synchronize(new MultiClusterData(H1));
            Assert.Equal(1, answer.Gateways.Count);
            Assert.True(answer.Gateways.ContainsKey(this.siloAddress1));
            Assert.Equal(G1, answer.Gateways[this.siloAddress1]);

            // push G2, retrieve H1
            answer = await this.gossipTable.Synchronize(new MultiClusterData(G2));
            Assert.Equal(1, answer.Gateways.Count);
            Assert.True(answer.Gateways.ContainsKey(this.siloAddress2));
            Assert.Equal(H1, answer.Gateways[this.siloAddress2]);

            // gossip stable
            await this.gossipTable.Publish(new MultiClusterData(H1));
            await this.gossipTable.Publish(new MultiClusterData(G1));
            answer = await this.gossipTable.Synchronize(new MultiClusterData(new GatewayEntry[] { H1, G2 }));
            Assert.True(answer.IsEmpty);

            // retrieve
            answer = await this.gossipTable.Synchronize(new MultiClusterData(new GatewayEntry[] { H1, G2 }));
            Assert.True(answer.IsEmpty);

            // push H2 
            await this.gossipTable.Publish(new MultiClusterData(H2));

            // retrieve all
            answer = await this.gossipTable.Synchronize(new MultiClusterData(new GatewayEntry[] { G1, H1 }));
            Assert.Equal(2, answer.Gateways.Count);
            Assert.True(answer.Gateways.ContainsKey(this.siloAddress1));
            Assert.True(answer.Gateways.ContainsKey(this.siloAddress2));
            Assert.Equal(G2, answer.Gateways[this.siloAddress1]);
            Assert.Equal(H2, answer.Gateways[this.siloAddress2]);
        }
         
    }
}
