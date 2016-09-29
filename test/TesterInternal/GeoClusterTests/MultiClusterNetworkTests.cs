using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.MultiCluster;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MultiClusterNetwork;
using Xunit;
using Xunit.Abstractions;

namespace Tests.GeoClusterTests
{
    public class MultiClusterNetworkTests : TestingClusterHost
    {
        public MultiClusterNetworkTests(ITestOutputHelper output) : base(output)
        { }

       

        // We need use ClientWrapper to load a client object in a new app domain. 
        // This allows us to create multiple clients that are connected to different silos.
        public class ClientWrapper : ClientWrapperBase
        {
            public ClientWrapper(string name, int gatewayport, string clusterId, Action<ClientConfiguration> customizer) : base(name, gatewayport, clusterId, customizer)
            {
                systemManagement = GrainClient.GrainFactory.GetGrain<IManagementGrain>(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);
            }
            IManagementGrain systemManagement;

            public MultiClusterConfiguration InjectMultiClusterConf(params string[] clusters)
            {
                return systemManagement.InjectMultiClusterConfiguration(clusters).Result;
            }

            public MultiClusterConfiguration GetMultiClusterConfiguration()
            {
                return systemManagement.GetMultiClusterConfiguration().Result;
            }

            public List<IMultiClusterGatewayInfo> GetMultiClusterGateways()
            {
                return systemManagement.GetMultiClusterGateways().Result;
            }

            public Dictionary<SiloAddress,SiloStatus> GetHosts()
            {
                return systemManagement.GetHosts().Result;
            }
        }


        [Fact, TestCategory("GeoCluster"), TestCategory("Functional")]
        public async Task TestMultiClusterConf_1_1()
        {
            // use a random global service id for testing purposes
            var globalserviceid = Guid.NewGuid();
         
            // create cluster A and clientA
            var clusterA = "A";
            NewGeoCluster(globalserviceid, clusterA, 1);
            var siloA = Clusters[clusterA].Silos[0].Silo.SiloAddress.Endpoint;
            var clientA = NewClient<ClientWrapper>(clusterA, 0);

            var cur = clientA.GetMultiClusterConfiguration();
            Assert.Null(cur); //no configuration should be there yet

            await WaitForMultiClusterGossipToStabilizeAsync(false);

            cur = clientA.GetMultiClusterConfiguration();
            Assert.Null(cur); //no configuration should be there yet

            var gateways = clientA.GetMultiClusterGateways();
            Assert.Equal(1,  gateways.Count);  // "Expect 1 gateway"
            Assert.Equal("A", gateways[0].ClusterId);
            Assert.Equal(siloA, gateways[0].SiloAddress.Endpoint);
            Assert.Equal(GatewayStatus.Active, gateways[0].Status);

            // create cluster B and clientB
            var clusterB = "B";
            NewGeoCluster(globalserviceid, clusterB, 1);
            var siloB = Clusters[clusterB].Silos[0].Silo.SiloAddress.Endpoint;
            var clientB = NewClient<ClientWrapper>(clusterB, 0);

            cur = clientB.GetMultiClusterConfiguration();
            Assert.Null(cur); //no configuration should be there yet

            await WaitForMultiClusterGossipToStabilizeAsync(false);

            cur = clientB.GetMultiClusterConfiguration();
            Assert.Null(cur); //no configuration should be there yet

            gateways = clientA.GetMultiClusterGateways();
            Assert.Equal(2,  gateways.Count);  // "Expect 2 gateways"
            gateways.Sort((a, b) => a.ClusterId.CompareTo(b.ClusterId));
            Assert.Equal(clusterA, gateways[0].ClusterId);
            Assert.Equal(siloA, gateways[0].SiloAddress.Endpoint);
            Assert.Equal(GatewayStatus.Active, gateways[0].Status);
            Assert.Equal(clusterB, gateways[1].ClusterId);
            Assert.Equal(siloB, gateways[1].SiloAddress.Endpoint);
            Assert.Equal(GatewayStatus.Active, gateways[1].Status);

            for (int i = 0; i < 2; i++)
            {
                // test injection
                var conf = clientA.InjectMultiClusterConf(clusterA, clusterB);

                // immediately visible on A, visible after stabilization on B
                cur = clientA.GetMultiClusterConfiguration();
                Assert.True(conf.Equals(cur));
                await WaitForMultiClusterGossipToStabilizeAsync(false);
                cur = clientA.GetMultiClusterConfiguration();
                Assert.True(conf.Equals(cur));
                cur = clientB.GetMultiClusterConfiguration();
                Assert.True(conf.Equals(cur));
            }

            // shut down cluster B
            StopSilo(Clusters[clusterB].Silos[0]);
            await WaitForLivenessToStabilizeAsync();

            // expect disappearance of gateway from multicluster network
            await WaitForMultiClusterGossipToStabilizeAsync(false);
            gateways = clientA.GetMultiClusterGateways();
            Assert.Equal(2,  gateways.Count);  // "Expect 2 gateways"
            var activegateways = gateways.Where(g => g.Status == GatewayStatus.Active).ToList();
            Assert.Equal(1,  activegateways.Count);  // "Expect 1 active gateway"
            Assert.Equal("A", activegateways[0].ClusterId);
        }

        private void AssertSameList(List<IMultiClusterGatewayInfo> a, List<IMultiClusterGatewayInfo> b)
        {
            Comparison<IMultiClusterGatewayInfo> comparer = (x, y) => x.SiloAddress.Endpoint.ToString().CompareTo(y.SiloAddress.Endpoint.ToString());
            a.Sort(comparer);
            b.Sort(comparer);
            Assert.Equal(a.Count,  b.Count);  // "number of gateways must match"
            for (int i = 0; i < a.Count; i++) {
                Assert.Equal(a[i].SiloAddress,  b[i].SiloAddress);  // "silo address at pos " + i + " must match"
                Assert.Equal(a[i].ClusterId,  b[i].ClusterId);  // "cluster id at pos " + i + " must match"
                Assert.Equal(a[i].Status,  b[i].Status);  // "status at pos " + i + " must match"
            }
        }

        [Fact, TestCategory("GeoCluster"), TestCategory("Functional")]
        public async Task TestMultiClusterConf_3_3()
        {
            // use a random global service id for testing purposes
            var globalserviceid = Guid.NewGuid();

            // use two clusters
            var clusterA = "A";
            var clusterB = "B";
            
            Action<ClusterConfiguration> configcustomizer = (ClusterConfiguration c) =>
            {
                c.Globals.DefaultMultiCluster = new List<string>(2) { clusterA, clusterB };

                // logging  
                foreach (var o in c.Overrides)
                {
                   o.Value.TraceLevelOverrides.Add(new Tuple<string, Severity>("Runtime.MultiClusterOracle", Severity.Verbose));
                }
            };
      
            // create cluster A and clientA
            NewGeoCluster(globalserviceid, clusterA, 3, configcustomizer);
            var clientA = NewClient<ClientWrapper>(clusterA, 0);
            var portA0 = Clusters[clusterA].Silos[0].Endpoint.Port;
            var portA1 = Clusters[clusterA].Silos[1].Endpoint.Port;
            var portA2 = Clusters[clusterA].Silos[2].Endpoint.Port;

            // create cluster B and clientB
            NewGeoCluster(globalserviceid, clusterB, 3, configcustomizer);
            var clientB = NewClient<ClientWrapper>(clusterB, 0);
            var portB0 = Clusters[clusterB].Silos[0].Endpoint.Port;
            var portB1 = Clusters[clusterB].Silos[1].Endpoint.Port;
            var portB2 = Clusters[clusterB].Silos[2].Endpoint.Port;

            // wait for membership to stabilize
            await WaitForLivenessToStabilizeAsync();
            // wait for gossip network to stabilize
            await WaitForMultiClusterGossipToStabilizeAsync(false);

            // check that default configuration took effect
            var cur = clientA.GetMultiClusterConfiguration();
            Assert.True(cur != null && string.Join(",", cur.Clusters) == string.Join(",", clusterA, clusterB));
            AssertSameList(clientA.GetMultiClusterGateways(), clientB.GetMultiClusterGateways());

            // expect 4 active gateways, two per cluster
            var activegateways = clientA.GetMultiClusterGateways().Where(g => g.Status == GatewayStatus.Active).ToList();
            Assert.Equal(string.Join(",", portA0, portA1),
                            string.Join(",", activegateways.Where(g => g.ClusterId == clusterA).Select(g => g.SiloAddress.Endpoint.Port).OrderBy(x => x)));
            Assert.Equal(string.Join(",", portB0, portB1),
                            string.Join(",", activegateways.Where(g => g.ClusterId == clusterB).Select(g => g.SiloAddress.Endpoint.Port).OrderBy(x => x)));
            var activegatewaysB = clientB.GetMultiClusterGateways().Where(g => g.Status == GatewayStatus.Active).ToList();
 
            // shut down one of the gateways in cluster B gracefully
            var target = Clusters[clusterB].Silos.Where(h => h.Endpoint.Port == portB1).FirstOrDefault();
            Assert.NotNull(target);
            StopSilo(target);
            await WaitForLivenessToStabilizeAsync();

            // expect disappearance and replacement of gateway from multicluster network
            await WaitForMultiClusterGossipToStabilizeAsync(false);
            AssertSameList(clientA.GetMultiClusterGateways(), clientB.GetMultiClusterGateways());
            activegateways = clientA.GetMultiClusterGateways().Where(g => g.Status == GatewayStatus.Active).ToList();
            Assert.Equal(string.Join(",", portA0, portA1),
                            string.Join(",", activegateways.Where(g => g.ClusterId == clusterA).Select(g => g.SiloAddress.Endpoint.Port).OrderBy(x => x)));
            Assert.Equal(string.Join(",", portB0, portB2),
                            string.Join(",", activegateways.Where(g => g.ClusterId == clusterB).Select(g => g.SiloAddress.Endpoint.Port).OrderBy(x => x)));
     

            // kill one of the gateways in cluster A
            target = Clusters[clusterA].Silos.Where(h => h.Endpoint.Port == portA1).FirstOrDefault();
            Assert.NotNull(target);
            KillSilo(target);
            await WaitForLivenessToStabilizeAsync();

            // wait for time necessary before peer removal can kick in
            await Task.Delay(MultiClusterOracle.CleanupSilentGoneGatewaysAfter);

            // wait for membership protocol to determine death of A
            while (true)
            {
                var hosts = clientA.GetHosts();
                var killedone = hosts.Where(kvp => kvp.Key.Endpoint.Port == portA1).FirstOrDefault();
                Assert.True(killedone.Value != SiloStatus.None);
                if (killedone.Value == SiloStatus.Dead)
                    break;
                await Task.Delay(100);
            }

            // wait for gossip propagation
            await WaitForMultiClusterGossipToStabilizeAsync(false);

            AssertSameList(clientA.GetMultiClusterGateways(), clientB.GetMultiClusterGateways());
            activegateways = clientA.GetMultiClusterGateways().Where(g => g.Status == GatewayStatus.Active).ToList();
            Assert.Equal(string.Join(",", portA0, portA2),
                            string.Join(",", activegateways.Where(g => g.ClusterId == clusterA).Select(g => g.SiloAddress.Endpoint.Port).OrderBy(x => x)));
            Assert.Equal(string.Join(",", portB0, portB2),
                            string.Join(",", activegateways.Where(g => g.ClusterId == clusterB).Select(g => g.SiloAddress.Endpoint.Port).OrderBy(x => x)));
        }
    }
}
