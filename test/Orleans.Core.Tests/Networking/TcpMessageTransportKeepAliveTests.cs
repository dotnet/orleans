using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Connections.Transport.Sockets;
using TestExtensions;
using Xunit;

namespace Orleans.Core.Tests.Networking;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class TcpMessageTransportKeepAliveTests
{
    private const string ListenerName = "silo";

    [Theory]
    [InlineData(false, true, 90, 30, 10)]
    [InlineData(true, true, 17, 7, 4)]
    [InlineData(true, false, 17, 7, 4)]
    public async Task Transports_ApplyKeepAliveSettingsToOutboundListeningAndAcceptedSockets(
        bool customizeOptions,
        bool enabled,
        int timeSeconds,
        int intervalSeconds,
        int retryCount)
    {
        var options = new TcpMessageTransportOptions { FastPath = false };
        if (customizeOptions)
        {
            options.KeepAlive = enabled;
            options.KeepAliveTimeSeconds = timeSeconds;
            options.KeepAliveIntervalSeconds = intervalSeconds;
            options.KeepAliveRetryCount = retryCount;
        }

        var tcpOptions = Substitute.For<IOptionsMonitor<TcpMessageTransportOptions>>();
        tcpOptions.CurrentValue.Returns(options);
        tcpOptions.Get(ListenerName).Returns(options);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        await using var listener = CreateListener(tcpOptions);
        await listener.BindAsync(cancellation.Token);
        var listenSocket = GetSocket(listener, "_listenSocket");
        AssertKeepAlive(listenSocket, enabled, timeSeconds, intervalSeconds, retryCount);

        var accept = listener.AcceptAsync(cancellation.Token).AsTask();
        await using var connector = new TcpMessageTransportConnector(tcpOptions, NullLoggerFactory.Instance);
        await using var outbound = await connector.CreateAsync(listenSocket.LocalEndPoint!, cancellation.Token);
        await using var inbound = Assert.IsType<SocketMessageTransport>(await accept);

        AssertKeepAlive(GetSocket(outbound, "_socket"), enabled, timeSeconds, intervalSeconds, retryCount);
        AssertKeepAlive(GetSocket(inbound, "_socket"), enabled, timeSeconds, intervalSeconds, retryCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Listener_AppliesCurrentNamedKeepAliveSettingsToAcceptedSockets(bool enabled)
    {
        var tcpOptions = Substitute.For<IOptionsMonitor<TcpMessageTransportOptions>>();
        var originalOptions = new TcpMessageTransportOptions { FastPath = false };
        tcpOptions.CurrentValue.Returns(originalOptions);
        tcpOptions.Get(ListenerName).Returns(originalOptions);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        await using var listener = CreateListener(tcpOptions);
        await listener.BindAsync(cancellation.Token);
        var listenSocket = GetSocket(listener, "_listenSocket");
        AssertKeepAlive(listenSocket, true, 90, 30, 10);

        tcpOptions.Get(ListenerName).Returns(new TcpMessageTransportOptions
        {
            KeepAlive = enabled,
            KeepAliveTimeSeconds = 19,
            KeepAliveIntervalSeconds = 6,
            KeepAliveRetryCount = 3
        });

        var accept = listener.AcceptAsync(cancellation.Token).AsTask();
        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await peer.ConnectAsync(listenSocket.LocalEndPoint!, cancellation.Token);
        await using var inbound = Assert.IsType<SocketMessageTransport>(await accept);

        AssertKeepAlive(GetSocket(inbound, "_socket"), enabled, 19, 6, 3);
        AssertKeepAlive(listenSocket, true, 90, 30, 10);
    }

    private static TcpMessageTransportListener CreateListener(IOptionsMonitor<TcpMessageTransportOptions> tcpOptions)
    {
        var listenerOptions = Substitute.For<IOptionsMonitor<TcpMessageTransportListenerOptions>>();
        listenerOptions.Get(ListenerName).Returns(new TcpMessageTransportListenerOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 0)
        });

        return new TcpMessageTransportListener(ListenerName, tcpOptions, listenerOptions, NullLoggerFactory.Instance);
    }

    private static Socket GetSocket(object owner, string fieldName) =>
        Assert.IsType<Socket>(owner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner));

    private static void AssertKeepAlive(Socket socket, bool enabled, int timeSeconds, int intervalSeconds, int retryCount)
    {
        Assert.Equal(enabled ? 1 : 0, (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!);
        if (enabled)
        {
            Assert.Equal(timeSeconds, (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime)!);
            Assert.Equal(intervalSeconds, (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval)!);
            Assert.Equal(retryCount, (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount)!);
        }
    }
}
