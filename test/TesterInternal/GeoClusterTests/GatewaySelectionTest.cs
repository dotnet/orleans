using System;
using System.Collections.Generic;
using System.Net;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Xunit;
using UpdateFaultCombo = Orleans.Runtime.MembershipService.MembershipOracleData.UpdateFaultCombo;

namespace UnitTests.GeoClusterTests
{
    /// <summary>
    /// Test selection algorithm for multi-cluster gateways
    /// </summary>
    public class GatewaySelectionTests
    {
        [Fact, TestCategory("GeoCluster"), TestCategory("Functional")]
        public void TestMultiClusterGatewaySelection()
        {
            var candidates = new SiloAddress[] {
                SiloAddress.New(new IPEndPoint(0,0),0),
                SiloAddress.New(new IPEndPoint(0,0),1),
                SiloAddress.New(new IPEndPoint(0,1),0),
                SiloAddress.New(new IPEndPoint(0,1),1),
                SiloAddress.New(new IPEndPoint(0,234),1),
                SiloAddress.New(new IPEndPoint(1,0),0),
                SiloAddress.New(new IPEndPoint(1,0),1),
                SiloAddress.New(new IPEndPoint(1,1),1),
                SiloAddress.New(new IPEndPoint(1,234),1),
                SiloAddress.New(new IPEndPoint(2,234),1),
                SiloAddress.New(new IPEndPoint(3,234),1),
                SiloAddress.New(new IPEndPoint(4,234),1),
            };
            
            Func<SiloAddress,UpdateFaultCombo> group = (SiloAddress a) => new UpdateFaultCombo(a.Endpoint.Port, a.Generation);

            // randomize order
            var r = new Random();
            var randomized = new SortedList<int,SiloAddress> ();
            foreach(var c in candidates)
                randomized.Add(r.Next(), c);

            var x = MembershipOracleData.DeterministicBalancedChoice(randomized.Values, 10, group);

            for (int i = 0; i < 10; i++)
                Assert.Equal(candidates[i], x[i]);
        }
    }
}
