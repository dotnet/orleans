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
using Tester;

namespace Tests.GeoClusterTests
{
    [TestCategory("GeoCluster")]
    public class MultiClusterNetworkTests : TestingClusterHost
    {
        public MultiClusterNetworkTests(ITestOutputHelper output) : base(output)
        {
        }

        // We need use ClientWrapper to load a client object in a new app domain. 
        // This allows us to create multiple clients that are connected to different silos.
        public class ClientWrapper : ClientWrapperBase
        {
            public static readonly Func<string, int, string, Action<ClientConfiguration>, Action<IClientBuilder>, ClientWrapper> Factory =
                (name, gwPort, clusterId, configUpdater, clientConfigurator) => new ClientWrapper(name, gwPort, clusterId, configUpdater, clientConfigurator);

            public ClientWrapper(string name, int gatewayport, string clusterId, Action<ClientConfiguration> customizer, Action<IClientBuilder> clientConfigurator) : base(name, gatewayport, clusterId, customizer, clientConfigurator)
            {
                this.systemManagement = this.GrainFactory.GetGrain<IManagementGrain>(0);
            }
            IManagementGrain systemManagement;

            public MultiClusterConfiguration InjectMultiClusterConf(params string[] clusters)
            {
                return systemManagement.InjectMultiClusterConfiguration(clusters).GetResult();
            }

            public MultiClusterConfiguration GetMultiClusterConfiguration()
            {
                return systemManagement.GetMultiClusterConfiguration().GetResult();
            }

            public List<IMultiClusterGatewayInfo> GetMultiClusterGateways()
            {
                return systemManagement.GetMultiClusterGateways().GetResult();
            }

            public Dictionary<SiloAddress,SiloStatus> GetHosts()
            {
                return systemManagement.GetHosts().GetResult();
            }
        }


        [SkippableFact, TestCategory("Functional")]
        public async Task TestMultiClusterConf_1_1()
        {
            // use a random global service id for testing purposes
            var globalserviceid = Guid.NewGuid();
         
            // create cluster A and clientA
            var clusterA = "A";
            NewGeoCluster(globalserviceid, clusterA, 1);
            var siloA = Clusters[clusterA].Silos.First().SiloAddress.Endpoint;
            var clientA = this.NewClient<ClientWrapper>(clusterA, 0, ClientWrapper.Factory);

            var cur = clientA.GetMultiClusterConfiguration();
            Assert.Null(cur); //no configuration should be there yet

            await WaitForMultiClusterGossipToStabilizeAsync(false);

            cur = clientA.GetMultiClusterConfiguration();
            Assert.Null(cur); //no configuration should be there yet

            var gateways = clientA.GetMultiClusterGateways();
            Assert.Single(gateways);  // "Expect 1 gateway"
            Assert.Equal("A", gateways[0].ClusterId);
            Assert.Equal(siloA, gateways[0].SiloAddress.Endpoint);
            Assert.Equal(GatewayStatus.Active, gateways[0].Status);

            // create cluster B and clientB
            var clusterB = "B";
            NewGeoCluster(globalserviceid, clusterB, 1);
            var siloB = Clusters[clusterB].Silos.First().SiloAddress.Endpoint;
            var clientB = NewClient<ClientWrapper>(clusterB, 0, ClientWrapper.Factory);

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
            Clusters[clusterB].Cluster.StopAllSilos();
            await WaitForLivenessToStabilizeAsync();

            // expect disappearance of gateway from multicluster network
            await WaitForMultiClusterGossipToStabilizeAsync(false);
            gateways = clientA.GetMultiClusterGateways();
            Assert.Equal(2,  gateways.Count);  // "Expect 2 gateways"
            var activegateways = gateways.Where(g => g.Status == GatewayStatus.Active).ToList();
            Assert.Single(activegateways);  // "Expect 1 active gateway"
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

        [SkippableFact(), TestCategory("Functional")]
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
            };
      
            // create cluster A and clientA
            NewGeoCluster(globalserviceid, clusterA, 3, configcustomizer);
            var clientA = this.NewClient<ClientWrapper>(clusterA, 0, ClientWrapper.Factory);
            var portsA = Clusters[clusterA].Cluster.GetActiveSilos().OrderBy(x => x.SiloAddress).Select(x => x.SiloAddress.Endpoint.Port).ToArray();

            // create cluster B and clientB
            NewGeoCluster(globalserviceid, clusterB, 3, configcustomizer);
            var clientB = this.NewClient<ClientWrapper>(clusterB, 0, ClientWrapper.Factory);
            var portsB = Clusters[clusterB].Cluster.GetActiveSilos().OrderBy(x => x.SiloAddress).Select(x => x.SiloAddress.Endpoint.Port).ToArray();

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
            Assert.Equal(string.Join(",", portsA[0], portsA[1]),
                            string.Join(",", activegateways.Where(g => g.ClusterId == clusterA).Select(g => g.SiloAddress.Endpoint.Port).OrderBy(x => x)));
            Assert.Equal(string.Join(",", portsB[0], portsB[1]),
                            string.Join(",", activegateways.Where(g => g.ClusterId == clusterB).Select(g => g.SiloAddress.Endpoint.Port).OrderBy(x => x)));
            var activegatewaysB = clientB.GetMultiClusterGateways().Where(g => g.Status == GatewayStatus.Active).ToList();
 
            // shut down one of the gateways in cluster B gracefully
            var target = Clusters[clusterB].Cluster.GetActiveSilos().Where(h => h.SiloAddress.Endpoint.Port == portsB[1]).FirstOrDefault();
            Assert.NotNull(target);
            Clusters[clusterB].Cluster.StopSilo(target);
            await WaitForLivenessToStabilizeAsync();

            // expect disappearance and replacement of gateway from multicluster network
            await WaitForMultiClusterGossipToStabilizeAsync(false);
            AssertSameList(clientA.GetMultiClusterGateways(), clientB.GetMultiClusterGateways());
            activegateways = clientA.GetMultiClusterGateways().Where(g => g.Status == GatewayStatus.Active).ToList();
            Assert.Equal(string.Join(",", portsA[0], portsA[1]),
                            string.Join(",", activegateways.Where(g => g.ClusterId == clusterA).Select(g => g.SiloAddress.Endpoint.Port).OrderBy(x => x)));
            Assert.Equal(string.Join(",", portsB[0], portsB[2]),
                            string.Join(",", activegateways.Where(g => g.ClusterId == clusterB).Select(g => g.SiloAddress.Endpoint.Port).OrderBy(x => x)));
     

            // kill one of the gateways in cluster A
            target = Clusters[clusterA].Cluster.GetActiveSilos().Where(h => h.SiloAddress.Endpoint.Port == portsA[1]).FirstOrDefault();
            Assert.NotNull(target);
            Clusters[clusterA].Cluster.KillSilo(target);
            await WaitForLivenessToStabilizeAsync();

            // wait for time necessary before peer removal can kick in
            await Task.Delay(MultiClusterOracle.CleanupSilentGoneGatewaysAfter);

            // wait for membership protocol to determine death of A
            while (true)
            {
                var hosts = clientA.GetHosts();
                var killedone = hosts.Where(kvp => kvp.Key.Endpoint.Port == portsA[1]).FirstOrDefault();
                Assert.True(killedone.Value != SiloStatus.None);
                if (killedone.Value == SiloStatus.Dead)
                    break;
                await Task.Delay(100);
            }

            // wait for gossip propagation
            await WaitForMultiClusterGossipToStabilizeAsync(false);

            AssertSameList(clientA.GetMultiClusterGateways(), clientB.GetMultiClusterGateways());
            activegateways = clientA.GetMultiClusterGateways().Where(g => g.Status == GatewayStatus.Active).ToList();
            Assert.Equal(string.Join(",", portsA[0], portsA[2]),
                            string.Join(",", activegateways.Where(g => g.ClusterId == clusterA).Select(g => g.SiloAddress.Endpoint.Port).OrderBy(x => x)));
            Assert.Equal(string.Join(",", portsB[0], portsB[2]),
                            string.Join(",", activegateways.Where(g => g.ClusterId == clusterB).Select(g => g.SiloAddress.Endpoint.Port).OrderBy(x => x)));
        }
    }
}
