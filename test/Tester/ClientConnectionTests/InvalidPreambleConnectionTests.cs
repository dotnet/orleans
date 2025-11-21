using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Tester.ClientConnectionTests
{
    /// <summary>
    /// Tests for handling invalid connection preambles sent to gateway endpoints.
    /// </summary>
    public class InvalidPreambleConnectionTests : TestClusterPerTest
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.ConnectionTransport = ConnectionTransportType.TcpSocket;
        }

        [Fact, TestCategory("Functional")]
        public async Task ShouldCloseConnectionWhenClientSendsInvalidPreambleSize()
        {
            var gateways = await this.HostedCluster.Client.ServiceProvider.GetRequiredService<IGatewayListProvider>().GetGateways();
            var gwEndpoint = gateways.First().ToIPEndPoint();

            using var socket = new Socket(gwEndpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            // Set receive timeout to avoid hanging indefinitely
            socket.ReceiveTimeout = 25_000;

            await socket.ConnectAsync(gwEndpoint);

            // Send invalid preamble size (exceeds MaxPreambleLength of 1024 from ConnectionPreambleHelper)
            int invalidSize = 99999;
            await socket.SendAsync(BitConverter.GetBytes(invalidSize), SocketFlags.None);

            // Try to read from the socket to detect closure
            // When the server closes the connection, Receive will return 0 bytes or throw
            var buffer = new byte[1];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            try
            {
                var bytesReceived = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token);

                // If we receive 0 bytes, the connection was gracefully closed by the server
                Assert.Equal(0, bytesReceived);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
            {
                // Connection was forcibly closed - this is also acceptable
            }
            catch (OperationCanceledException)
            {
                Assert.Fail("Server did not close the connection within the timeout period");
            }
        }
    }
}
