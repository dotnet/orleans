using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
    [InlineData(SocketOptionName.KeepAlive, SocketError.ProtocolOption, false)]
    [InlineData(SocketOptionName.TcpKeepAliveTime, SocketError.ProtocolOption, false)]
    [InlineData(SocketOptionName.TcpKeepAliveInterval, SocketError.OperationNotSupported, false)]
    [InlineData(SocketOptionName.TcpKeepAliveRetryCount, SocketError.ProtocolNotSupported, false)]
    [InlineData(SocketOptionName.TcpKeepAliveRetryCount, SocketError.SocketNotSupported, false)]
    [InlineData(SocketOptionName.TcpKeepAliveTime, SocketError.Success, true)]
    public void ConfigureKeepAlive_UnsupportedOption_LogsAndRetainsSupportedSettings(
        SocketOptionName unsupportedOption, SocketError socketError, bool platformException)
    {
        Exception failure = platformException
            ? new PlatformNotSupportedException()
            : new SocketException((int)socketError);
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);
        var attempted = new List<(SocketOptionLevel, SocketOptionName, int)>();
        var applied = new List<SocketOptionName>();

        SocketExtensions.ConfigureKeepAlive(new TcpMessageTransportOptions(), logger, (level, option, value) =>
        {
            attempted.Add((level, option, value));
            if (option == unsupportedOption)
            {
                throw failure;
            }

            applied.Add(option);
        });

        var expected = unsupportedOption == SocketOptionName.KeepAlive
            ? new[] { (SocketOptionLevel.Socket, SocketOptionName.KeepAlive, 1) }
            : new[]
            {
                (SocketOptionLevel.Socket, SocketOptionName.KeepAlive, 1),
                (SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 90),
                (SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 30),
                (SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 10)
            };
        Assert.Equal(expected, attempted);
        Assert.Equal(expected.Select(entry => entry.Item2).Where(option => option != unsupportedOption), applied);
        var log = Assert.Single(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILogger.Log));
        Assert.Equal(LogLevel.Warning, log.GetArguments()[0]);
        Assert.Equal("UnsupportedKeepAliveOption", Assert.IsType<EventId>(log.GetArguments()[1]).Name);
        Assert.Contains(unsupportedOption.ToString(), log.GetArguments()[2]!.ToString());
        Assert.Same(failure, log.GetArguments()[3]);
    }

    [Theory]
    [InlineData(SocketError.InvalidArgument)]
    [InlineData(SocketError.AccessDenied)]
    [InlineData(SocketError.NotSocket)]
    public void ConfigureKeepAlive_OtherSocketErrors_Propagate(SocketError socketError)
    {
        var failure = new SocketException((int)socketError);
        var logger = Substitute.For<ILogger>();
        var attempted = new List<SocketOptionName>();

        var thrown = Assert.Throws<SocketException>(() =>
            SocketExtensions.ConfigureKeepAlive(new TcpMessageTransportOptions(), logger, (_, option, _) =>
            {
                attempted.Add(option);
                if (option == SocketOptionName.TcpKeepAliveTime)
                {
                    throw failure;
                }
            }));

        Assert.Same(failure, thrown);
        Assert.Equal([SocketOptionName.KeepAlive, SocketOptionName.TcpKeepAliveTime], attempted);
        Assert.DoesNotContain(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILogger.Log));
    }

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
