using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
public class TestClusterPortAllocatorTests
{
    [Fact]
    public void GetAvailableConsecutiveServerPorts_WhenSocketBindFails_ReportsRejectionDiagnostics()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();
        var port = Assert.IsType<IPEndPoint>(listener.LocalEndPoint).Port;
        using var allocator = new TestClusterPortAllocator();

        var exception = Assert.Throws<InvalidOperationException>(
            () => allocator.GetAvailableConsecutiveServerPorts([], port, port + 1, consecutivePortsToCheck: 1));

        Assert.Contains($"range [{port}, {port + 1}) after 100 attempts in ", exception.Message);
        Assert.Contains("active TCP listener=0", exception.Message);
        Assert.Contains($"socket bind failure=100 (last failure: {port} ", exception.Message);
        Assert.Contains("reservation held by this process=0", exception.Message);
        Assert.Contains("reservation held by another process=0", exception.Message);
    }

    [Fact]
    public void GetAvailableConsecutiveServerPorts_WhenReservedByCurrentProcess_ReportsAndReleasesReservation()
    {
        var port = GetAvailablePort();
        using var firstAllocator = new TestClusterPortAllocator();
        using var secondAllocator = new TestClusterPortAllocator();
        using var thirdAllocator = new TestClusterPortAllocator();
        Assert.Equal(port, firstAllocator.GetAvailableConsecutiveServerPorts([], port, port + 1, consecutivePortsToCheck: 1));

        var exception = Assert.Throws<InvalidOperationException>(
            () => secondAllocator.GetAvailableConsecutiveServerPorts([], port, port + 1, consecutivePortsToCheck: 1));

        Assert.Contains("socket bind failure=0", exception.Message);
        Assert.Contains($"reservation held by this process=100 (last port: {port})", exception.Message);
        Assert.Contains("reservation held by another process=0", exception.Message);

        firstAllocator.Dispose();
        Assert.Equal(port, thirdAllocator.GetAvailableConsecutiveServerPorts([], port, port + 1, consecutivePortsToCheck: 1));
    }

    private static int GetAvailablePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return Assert.IsType<IPEndPoint>(socket.LocalEndPoint).Port;
    }
}
