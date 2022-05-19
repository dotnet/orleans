using System;
using System.Net.Sockets;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Tester.TransportTests;

public class UnixSocketTransportTests : TransportTestsBase, IClassFixture<UnixSocketTransportTests.Fixture>
{
    public UnixSocketTransportTests(Fixture fixture) : base(fixture)
    {
    }

    public class Fixture : BaseTestClusterFixture
    {
        protected override void CheckPreconditionsOrThrow()
        {
            try
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressFamilyNotSupported)
            {
                throw new SkipException("Unix socket not supported", ex);
            }
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.ConnectionTransport = ConnectionTransportType.UnixSocket;
        }
    }
}
