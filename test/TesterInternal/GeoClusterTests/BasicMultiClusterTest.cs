using System;
using System.Collections.Generic;
using Orleans;
using Orleans.Runtime;
using Xunit;
using Xunit.Abstractions;
using Orleans.Runtime.Configuration;

namespace Tests.GeoClusterTests
{
    public class BasicMultiClusterTest 
    {

        // We use ClientWrapper to load a client object in a new app domain. 
        // This allows us to create multiple clients that are connected to different silos.
        // this client is used to call into the management grain.
        public class ClientWrapper : TestingClusterHost.ClientWrapperBase
        {
            public ClientWrapper(string name, int gatewayport, string clusterId, Action<ClientConfiguration> customizer)
                // use null clusterId, in this test, because we are testing non-geo clients
                : base(name, gatewayport, null, customizer)
            {
                this.systemManagement = this.GrainFactory.GetGrain<IManagementGrain>(0);
            }
            IManagementGrain systemManagement;

            public Dictionary<SiloAddress, SiloStatus> GetHosts()
            {
                return systemManagement.GetHosts().GetResult();
            }
        }

        public BasicMultiClusterTest(ITestOutputHelper output)
        {
            this.output = output;
        }
        private ITestOutputHelper output;

        [Fact, TestCategory("GeoCluster"), TestCategory("Functional")]
        public void CreateTwoIndependentClusters()
        {
            using (var host = new TestingClusterHost(output))
            {
                // create cluster A with one silo and clientA
                var clusterA = "A";
                host.NewCluster(clusterA, 1);
                var clientA = host.NewClient<ClientWrapper>(clusterA, 0);

                // create cluster B with 5 silos and clientB
                var clusterB = "B";
                host.NewCluster(clusterB, 5);
                var clientB = host.NewClient<ClientWrapper>(clusterB, 0);

                // call management grain in each cluster to count the silos
                var silos_in_A = clientA.GetHosts().Count;
                var silos_in_B = clientB.GetHosts().Count;

                Assert.Equal(1, silos_in_A);
                Assert.Equal(5, silos_in_B);
            }
        }
    }
}
