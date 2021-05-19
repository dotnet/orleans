using System.Net;
using Orleans.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.UtilsTests
{
    [TestCategory("Utils")]
    public class UtilsTests
    {
        private readonly ITestOutputHelper output;


        public UtilsTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact, TestCategory("BVT")]
        public void ToGatewayUriTest()
        {
            var ipv4 = new IPEndPoint(IPAddress.Any, 11111);
            var uri = ipv4.ToGatewayUri();
            Assert.Equal("gwy.tcp://0.0.0.0:11111/0", uri.ToString());

            var ipv4silo = SiloAddress.New(ipv4, 100);
            uri = ipv4silo.ToGatewayUri();
            Assert.Equal("gwy.tcp://0.0.0.0:11111/100", uri.ToString());

            var ipv6 = new IPEndPoint(IPAddress.IPv6Any, 11111);
            uri = ipv6.ToGatewayUri();
            Assert.Equal("gwy.tcp://[::]:11111/0", uri.ToString());

            var ipv6silo = SiloAddress.New(ipv6, 100);
            uri = ipv6silo.ToGatewayUri();
            Assert.Equal("gwy.tcp://[::]:11111/100", uri.ToString());
        }
    }
}
