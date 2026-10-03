#nullable enable
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Connections.Transport;
using Orleans.Messaging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ClientOutboundConnectionFactory(
        IOptions<ConnectionOptions> connectionOptions,
        IOptions<ClusterOptions> clusterOptions,
        MessageTransportConnector connector,
        IEnumerable<IMessageTransportConnectorMiddleware> connectorMiddleware,
        ConnectionCommon connectionShared,
        ConnectionPreambleHelper connectionPreambleHelper) : ConnectionFactory(connector, connectorMiddleware)
    {
        private readonly object _initializationLock = new();
        private readonly ConnectionCommon _connectionShared = connectionShared;
        private readonly ConnectionOptions _connectionOptions = connectionOptions.Value;
        private readonly ClusterOptions _clusterOptions = clusterOptions.Value;
        private readonly ConnectionPreambleHelper _connectionPreambleHelper = connectionPreambleHelper;
        private volatile bool _isInitialized;
        private ClientMessageCenter? _messageCenter;
        private ConnectionManager? _connectionManager;

        protected override Connection CreateConnection(SiloAddress address, MessageTransport transport)
        {
            EnsureInitialized();

            return new ClientOutboundConnection(
                address,
                transport,
                _messageCenter,
                _connectionManager,
                _connectionShared,
                _connectionOptions,
                _connectionPreambleHelper,
                _clusterOptions);
        }

        protected override EndPoint GetEndPoint(SiloAddress address) => address.Endpoint;

        [MemberNotNull(nameof(_messageCenter), nameof(_connectionManager))]
        private void EnsureInitialized()
        {
            if (!_isInitialized)
            {
                lock (_initializationLock)
                {
                    if (!_isInitialized)
                    {
                        _messageCenter = _connectionShared.ServiceProvider.GetRequiredService<ClientMessageCenter>();
                        _connectionManager = _connectionShared.ServiceProvider.GetRequiredService<ConnectionManager>();
                        _isInitialized = true;
                    }
                }
            }

            Debug.Assert(_messageCenter is not null);
            Debug.Assert(_connectionManager is not null);
        }
    }
}
