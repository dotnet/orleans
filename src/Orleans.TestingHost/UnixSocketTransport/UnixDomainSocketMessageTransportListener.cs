#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Connections.Transport;
using Orleans.Connections.Transport.Sockets;

namespace Orleans.TestingHost.UnixSocketTransport;

public class UnixDomainSocketMessageTransportListenerOptions
{
    public string Path { get; set; } = CreateDefaultPath();
    public bool Enabled { get; set; } = true;
    private static string CreateDefaultPath() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"silo_{Guid.NewGuid():N}");
}

internal class UnixDomainSocketMessageTransportListener : MessageTransportListener
{
    private readonly CancellationTokenSource _closingCts = new();
    private Socket? _listenSocket;
    private string? _boundPath;
    private readonly IOptionsMonitor<UnixDomainSocketMessageTransportListenerOptions> _listenerOptions;

    internal UnixDomainSocketMessageTransportListener(
        string endpointName,
        IOptionsMonitor<UnixDomainSocketMessageTransportListenerOptions> listenerOptions,
        ILoggerFactory loggerFactory)
    {
        ListenerName = endpointName;
        _listenerOptions = listenerOptions;
        Logger = loggerFactory.CreateLogger("Orleans.Connections.Transport.Sockets");
    }

    protected ILogger Logger { get; }

    /// <inheritdoc/>
    public override FeatureCollection Features { get; } = new FeatureCollection();

    /// <inheritdoc/>
    public override bool IsValid => Socket.OSSupportsUnixDomainSockets && _listenerOptions.Get(ListenerName).Enabled;

    /// <inheritdoc/>
    public override string ListenerName { get; }

    protected virtual Socket CreateListenSocket()
    {
        var listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        return listenSocket;
    }

    public override ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        if (_listenSocket != null)
        {
            throw new InvalidOperationException("Transport already bound");
        }

        var listenSocket = CreateListenSocket();

        var options = _listenerOptions.Get(ListenerName);
        var path = options.Path;
        var bound = false;
        try
        {
            listenSocket.Bind(new UnixDomainSocketEndPoint(path));
            bound = true;
            listenSocket.Listen(512);
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            listenSocket.Dispose();
            throw new AddressInUseException(e.Message, e);
        }
        catch
        {
            listenSocket.Dispose();
            if (bound && !string.IsNullOrEmpty(path) && path[0] != '\0')
            {
                File.Delete(path);
            }

            throw;
        }

        _boundPath = path;
        _listenSocket = listenSocket;
        return default;
    }

    public override async ValueTask<MessageTransport?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var listenSocket = _listenSocket ?? throw new InvalidOperationException("Transport is not bound");
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closingCts.Token);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var acceptSocket = await listenSocket.AcceptAsync(ct.Token).ConfigureAwait(false);
                var connection = new SocketMessageTransport(acceptSocket, Logger);
                connection.Start();

                return connection;
            }
            catch (OperationCanceledException)
            {
                // Graceful termination.
                return null;
            }
            catch (ObjectDisposedException)
            {
                // A call was made to UnbindAsync/DisposeAsync just return null which signals we're done
                return null;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.OperationAborted)
            {
                // A call was made to UnbindAsync/DisposeAsync just return null which signals we're done
                return null;
            }
            catch (SocketException)
            {
                // The connection got reset while it was in the backlog, so we try again.
                SocketsLog.ConnectionReset(Logger, connection: "(null)");
            }
        }

        return null;
    }

    private void DisposeCore()
    {
        _closingCts.Cancel();
        _listenSocket?.Dispose();
        _listenSocket = null;

        var path = _boundPath;
        _boundPath = null;
        if (!string.IsNullOrEmpty(path) && path[0] != '\0')
        {
            File.Delete(path);
        }
    }

    public override ValueTask UnbindAsync(CancellationToken cancellationToken)
    {
        DisposeCore();
        return default;
    }

    public override async ValueTask DisposeAsync()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            _closingCts.Dispose();
        }
    }
}
