using System;
using System.Linq;
using System.Net;
using Orleans.MultiCluster;
using Orleans.Runtime;
using Orleans.Runtime.MultiClusterNetwork;
using Xunit;

namespace Tester.GeoClusterTests
{
    /// <summary>
    /// Test gossip channel functionality
    /// </summary>
    public class MultiClusterDataTests
    {
        [Fact, TestCategory("GeoCluster"), TestCategory("BVT"), TestCategory("Functional")]
        public void MultiClusterData_Configuration()
        {
            var ts1 = new DateTime(year: 2011, month: 1, day: 1);
            var ts2 = new DateTime(year: 2012, month: 2, day: 2);

            var conf1 = new MultiClusterConfiguration(ts1, new string[] { "A" }.ToList());
            var conf2 = new MultiClusterConfiguration(ts2, new string[] { "A", "B", "C" }.ToList());

            var gd1 = new MultiClusterData();
            var gd2 = new MultiClusterData(conf1);
            var gd3 = new MultiClusterData(conf2);

            TestAlgebraicProperties(gd1, gd1);
            TestAlgebraicProperties(gd2, gd2);
            TestAlgebraicProperties(gd1, gd2);
            TestAlgebraicProperties(gd3, gd1);
            TestAlgebraicProperties(gd2, gd3);
            TestAlgebraicProperties(gd3, gd2);
        }

        [Fact, TestCategory("GeoCluster"), TestCategory("BVT"), TestCategory("Functional")]
        public void MultiClusterData_Gateways()
        {
            var ts1 = DateTime.UtcNow;
            var ts2 = ts1 + new TimeSpan(hours: 0, minutes: 0, seconds: 1);
            var ts3 = ts1 + new TimeSpan(hours: 0, minutes: 0, seconds: 2);

            IPAddress ip;
            Assert.True(IPAddress.TryParse("127.0.0.1", out ip));
            IPEndPoint ep1 = new IPEndPoint(ip, 21111);
            var siloAddress1 = SiloAddress.New(ep1, 0);
            IPEndPoint ep2 = new IPEndPoint(ip, 21112);
            var siloAddress2 = SiloAddress.New(ep2, 0);

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


            var gd1 = new MultiClusterData();
            var gd2 = new MultiClusterData(G1);
            var gd3 = new MultiClusterData(G2);

            TestAlgebraicProperties(gd1, gd1);
            TestAlgebraicProperties(gd2, gd2);
            TestAlgebraicProperties(gd1, gd2);
            TestAlgebraicProperties(gd3, gd1);
            TestAlgebraicProperties(gd2, gd3);
            TestAlgebraicProperties(gd3, gd2);

            gd1 = new MultiClusterData(new GatewayEntry[] { H1, G2 });
            gd2 = new MultiClusterData(new GatewayEntry[] { H2, G1 });

            TestAlgebraicProperties(gd1, gd1);
            TestAlgebraicProperties(gd1, gd2);
            TestAlgebraicProperties(gd2, gd1);

            gd1 = new MultiClusterData(new GatewayEntry[] { H1, G2 });
            gd2 = new MultiClusterData(new GatewayEntry[] { });

            TestAlgebraicProperties(gd1, gd2);
            TestAlgebraicProperties(gd2, gd1);

            gd1 = new MultiClusterData(new GatewayEntry[] { H1, G2 });
            gd2 = new MultiClusterData(new GatewayEntry[] { H1, G1 });

            TestAlgebraicProperties(gd1, gd2);
            TestAlgebraicProperties(gd2, gd1);

            gd1 = new MultiClusterData(new GatewayEntry[] { H1, G2 });
            gd2 = new MultiClusterData(new GatewayEntry[] { G1 });

            TestAlgebraicProperties(gd1, gd2);
            TestAlgebraicProperties(gd2, gd1);

            gd1 = new MultiClusterData(new GatewayEntry[] { H1, G2 });
            gd2 = new MultiClusterData(new GatewayEntry[] { H2 });

            TestAlgebraicProperties(gd1, gd2);
            TestAlgebraicProperties(gd2, gd1);
        }

        private void TestAlgebraicProperties(MultiClusterData A, MultiClusterData B)
        {
            MultiClusterData D;
            var BB = B.Merge(A, out D);
            var empty = new MultiClusterData();

            AssertEffect(D, B, BB, D);
            AssertEffect(D, BB, BB, empty);
            AssertEffect(D, A, A, empty);

            AssertEffect(BB.Minus(D), B, B, empty);
            AssertEffect(BB.Minus(D), A, BB);
            AssertEffect(B, A, BB);
        }

        private void AssertEffect(MultiClusterData what, MultiClusterData to, MultiClusterData expectedMerge, MultiClusterData expectedDelta = null)
        {
            MultiClusterData delta;
            var merge = to.Merge(what, out delta);

            Assert.True(CheckEquality(expectedMerge, merge));

            if (expectedDelta != null)
                Assert.Equal(expectedDelta, expectedDelta);
        }

        private bool CheckEquality(MultiClusterData one, MultiClusterData other)
        {
            if (one == null) return (other == null);
            if (other == null) return false;

            if ((one.Configuration == null) != (other.Configuration == null))
                return false;

            if (one.Gateways.Count != other.Gateways.Count)
                return false;

            if ((one.Configuration != null) && !one.Configuration.Equals(other.Configuration))
                return false;

            foreach (var g in one.Gateways)
            {
                GatewayEntry othergateway;
                if (!other.Gateways.TryGetValue(g.Key, out othergateway))
                    return false;
                if (!g.Value.Equals(othergateway))
                    return false;
            }

            return true;
        }

    }
}

