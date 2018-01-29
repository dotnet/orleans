using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Tester.ClientConnectionTests
{
    public class InvalidPreambleConnectionTests : TestClusterPerTest
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.ConfigureLegacyConfiguration();
        }

        [Fact, TestCategory("Functional")]
        public void ShouldCloseConnectionWhenClientSendsInvalidPreambleSize()
        {
            var gwEndpoint = this.HostedCluster.Client.Configuration.Gateways.First();

            using (Socket s = new Socket(gwEndpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
            {
                s.Connect(gwEndpoint);

                Int32 invalidSize = 99999;
                s.Send(BitConverter.GetBytes(invalidSize));

                bool socketClosed = s.Poll(100000, SelectMode.SelectRead) && s.Available == 0;
                Assert.True(socketClosed);
            }
        }
    }
}
