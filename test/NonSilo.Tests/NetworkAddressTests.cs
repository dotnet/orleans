using Orleans.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace NonSilo.Tests
{
    [TestCategory("BVT")]
    public class NetworkAddressTests
    {
        [Fact]
        public void SubnetInclusion()
        {
            var netAddress = new NetworkAddress
            {
                Address = IPAddress.Parse("10.0.0.0"),
                Mask = IPAddress.Parse("255.0.0.0")
            };
            Assert.True(netAddress.Contains(IPAddress.Parse("10.0.0.1")));
            Assert.True(netAddress.Contains(IPAddress.Parse("10.0.1.1")));
            Assert.True(netAddress.Contains(IPAddress.Parse("10.1.1.1")));
            Assert.True(netAddress.Contains(IPAddress.Parse("10.254.1.1")));
            Assert.False(netAddress.Contains(IPAddress.Parse("172.16.1.1")));

            netAddress.Mask = IPAddress.Parse("255.128.0.0");
            Assert.True(netAddress.Contains(IPAddress.Parse("10.0.0.1")));
            Assert.True(netAddress.Contains(IPAddress.Parse("10.0.1.1")));
            Assert.True(netAddress.Contains(IPAddress.Parse("10.1.1.1")));
            Assert.False(netAddress.Contains(IPAddress.Parse("10.254.1.1")));
            Assert.False(netAddress.Contains(IPAddress.Parse("172.16.1.1")));

            netAddress.Mask = IPAddress.Parse("255.255.0.0");
            Assert.True(netAddress.Contains(IPAddress.Parse("10.0.0.1")));
            Assert.True(netAddress.Contains(IPAddress.Parse("10.0.1.1")));
            Assert.False(netAddress.Contains(IPAddress.Parse("10.1.1.1")));
            Assert.False(netAddress.Contains(IPAddress.Parse("10.254.1.1")));
            Assert.False(netAddress.Contains(IPAddress.Parse("172.16.1.1")));

            netAddress.Mask = IPAddress.Parse("255.255.255.0");
            Assert.True(netAddress.Contains(IPAddress.Parse("10.0.0.1")));
            Assert.False(netAddress.Contains(IPAddress.Parse("10.0.1.1")));
            Assert.False(netAddress.Contains(IPAddress.Parse("10.1.1.1")));
            Assert.False(netAddress.Contains(IPAddress.Parse("10.254.1.1")));
            Assert.False(netAddress.Contains(IPAddress.Parse("172.16.1.1")));

            netAddress.Mask = IPAddress.Parse("255.255.255.252");
            Assert.True(netAddress.Contains(IPAddress.Parse("10.0.0.1")));
            Assert.True(netAddress.Contains(IPAddress.Parse("10.0.0.2")));
            Assert.True(netAddress.Contains(IPAddress.Parse("10.0.0.3")));
            Assert.False(netAddress.Contains(IPAddress.Parse("10.0.0.4")));
        }

        [Fact]
        public void SelectIpv4ByDefault()
        {
            var option = new EndpointOptions
            {
                Port = 3000,
            };
            var resolvedEndpoint = option.ResolveEndpoint();

            Assert.Equal(AddressFamily.InterNetwork, resolvedEndpoint.AddressFamily);
            Assert.True(option.NetworkAddress.Contains(resolvedEndpoint.Address));
        }

        [SkippableFact]
        public void SelectIpv6()
        {
            var ipAddresses = Dns.GetHostAddresses(Dns.GetHostName());
            Skip.IfNot(
                ipAddresses.Any(ip => ip.AddressFamily == AddressFamily.InterNetworkV6),
                "IPv6 is not available on this machine");

            var option = new EndpointOptions
            {
                NetworkAddress = new NetworkAddress(IPAddress.IPv6Any, IPAddress.IPv6Any),
                Port = 3000,
            };
            var resolvedEndpoint = option.ResolveEndpoint();

            Assert.Equal(AddressFamily.InterNetworkV6, resolvedEndpoint.AddressFamily);
            Assert.True(option.NetworkAddress.Contains(resolvedEndpoint.Address));
        }
    }
}
