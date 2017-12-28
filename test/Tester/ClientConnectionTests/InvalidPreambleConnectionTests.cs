using System;
using System.Net.Sockets;
using TestExtensions;
using Xunit;

namespace Tester.ClientConnectionTests
{
    public class InvalidPreambleConnectionTests : TestClusterPerTest
    {
        [Fact, TestCategory("Functional")]
        public void ShouldCloseConnectionWhenClientSendsInvalidPreambleSize()
        {
            var gwEndpoint = this.HostedCluster.Primary.NodeConfiguration.ProxyGatewayEndpoint;

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
