#nullable enable

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Net;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Connections.Transport.Sockets;

public class TcpMessageTransportOptions
{
    // We can expose these eventually, if desired.
    internal LingerOption LingerOption { get; set; } = new LingerOption(true, 0);
    internal bool NoDelay { get; set; } = true;
    internal bool FastPath { get; set; } = true;
    internal bool DualMode { get; set; } = true;
}

/// <summary>
/// <see cref="MessageTransportConnector"/> which creates TCP connections.
/// </summary>
public class TcpMessageTransportConnector : MessageTransportConnector
{
    public const string EndpointAddressPropertyName = "ep";
    private readonly IOptionsMonitor<TcpMessageTransportOptions> _options;
    private readonly ILogger _logger;

    [SetsRequiredMembers]
    public TcpMessageTransportConnector(IOptionsMonitor<TcpMessageTransportOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _logger = loggerFactory.CreateLogger("Orleans.Connections.Transport.Sockets");
    }

    /// <inheritdoc/>
    public override IFeatureCollection Features { get; } = new FeatureCollection();

    /// <inheritdoc/>
    public override bool IsValid => true;

    /// <inheritdoc/>
    public override async ValueTask<MessageTransport> CreateAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        if (endPoint is not IPEndPoint ip)
        {
            throw new ConnectionAbortedException($"Endpoint {endPoint} is not a TCP endpoint");
        }

        var options = _options.CurrentValue;

        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            LingerState = options.LingerOption,
            NoDelay = options.NoDelay
        };

        if (options.FastPath)
        {
            socket.EnableFastPath(noDelay: options.NoDelay);
        }

        var completion = new SingleUseAwaitableSocketAsyncEventArgs
        {
            RemoteEndPoint = ip,
        };

        try
        {
            using var _ = cancellationToken.Register(static state => ((SingleUseAwaitableSocketAsyncEventArgs)state!).Cancel(), completion);

            if (!socket.ConnectAsync(completion))
            {
                completion.Complete();
            }

            if (!await completion)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (completion.SocketError != SocketError.Success)
            {
                throw new SocketConnectionException($"Unable to connect to {ip}. Error: {completion.SocketError}");
            }

            var connection = new SocketMessageTransport(socket, _logger);
            connection.Start();
            return connection;
        }
        catch
        {
            socket.Dispose();
            completion.Dispose();
            throw;
        }
    }

    private class SingleUseAwaitableSocketAsyncEventArgs : SocketAsyncEventArgs, ICriticalNotifyCompletion
    {
        private readonly TaskCompletionSource<bool> _completion = new();

        public TaskAwaiter<bool> GetAwaiter() => _completion.Task.GetAwaiter();

        public void Cancel()
        {
            _completion.TrySetResult(false);
        }

        public void Complete() => _completion.TrySetResult(true);

        public void OnCompleted(Action continuation) => GetAwaiter().OnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation) => GetAwaiter().UnsafeOnCompleted(continuation);

        protected override void OnCompleted(SocketAsyncEventArgs _) => _completion.TrySetResult(true);
    }
}
