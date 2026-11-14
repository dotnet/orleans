using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Connections.Transport;
using Orleans.Internal;

namespace Orleans.Runtime.Messaging;

internal abstract class ConnectionFactory : IDisposable, IAsyncDisposable
{
    private readonly MessageTransportConnector _transportConnector;
    private readonly AdmissionGate _connectionEstablishment = new();
    private readonly object _disposeLock = new();
    private Task? _disposeTask;

    protected ConnectionFactory(MessageTransportConnector transportConnector, IEnumerable<IMessageTransportConnectorMiddleware> middleware)
    {
        MessageTransportConnector connector = new BorrowedConnector(transportConnector);
        foreach (var mw in middleware)
        {
            connector = mw.Apply(connector);
        }

        _transportConnector = connector;
    }

    protected abstract Connection CreateConnection(SiloAddress address, MessageTransport context);

    public virtual async ValueTask<Connection> ConnectAsync(SiloAddress address, CancellationToken cancellationToken)
    {
        using var admission = _connectionEstablishment.TryEnter();
        ObjectDisposedException.ThrowIf(!admission.Entered, this);

        // Connect to the endpoint.
        var transport = await _transportConnector.CreateAsync(GetEndPoint(address), cancellationToken).ConfigureAwait(false);

        // Create a connection object to represent the connection.
        try
        {
            return CreateConnection(address, transport);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        lock (_disposeLock)
        {
            return new(_disposeTask ??= DisposeAsyncCore());
        }
    }

    private async Task DisposeAsyncCore()
    {
        await _connectionEstablishment.CloseAsync().ConfigureAwait(false);
        await _transportConnector.DisposeAsync().ConfigureAwait(false);
    }

    protected abstract EndPoint GetEndPoint(SiloAddress address);

    private sealed class BorrowedConnector(MessageTransportConnector connector) : MessageTransportConnector
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Usage",
            "CA2213:Disposable fields should be disposed",
            Justification = "The service provider owns the registered connector. This adapter lets the factory dispose middleware-owned decorators independently.")]
        private readonly MessageTransportConnector _connector = connector;

        public override IFeatureCollection Features => _connector.Features;
        public override bool IsValid => _connector.IsValid;
        public override ValueTask<MessageTransport> CreateAsync(EndPoint endpoint, CancellationToken cancellationToken = default) =>
            _connector.CreateAsync(endpoint, cancellationToken);
    }
}
