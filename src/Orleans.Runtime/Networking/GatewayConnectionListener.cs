using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Connections.Transport;

#nullable disable
namespace Orleans.Runtime.Messaging
{
    internal sealed class GatewayConnectionListener(
        IEnumerable<MessageTransportListener> listeners,
        IEnumerable<IMessageTransportListenerMiddleware> listenerMiddleware,
        IOptions<ConnectionOptions> connectionOptions,
        OverloadDetector overloadDetector,
        ILocalSiloDetails localSiloDetails,
        IOptions<EndpointOptions> endpointOptions,
        MessageCenter messageCenter,
        ConnectionManager connectionManager,
        ConnectionCommon connectionShared,
        ConnectionPreambleHelper connectionPreambleHelper,
        ILogger<GatewayConnectionListener> logger) : ConnectionListener(
              listeners.Where(static listener => listener.ListenerName.Equals(DefaultListenerName, StringComparison.Ordinal)),
              listenerMiddleware,
              connectionOptions,
              connectionManager,
              connectionShared), ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver
    {
        public const string DefaultListenerName = "gateway";
        private readonly ILocalSiloDetails _localSiloDetails = localSiloDetails;
        private readonly MessageCenter _messageCenter = messageCenter;
        private readonly ConnectionCommon _connectionShared = connectionShared;
        private readonly ConnectionPreambleHelper _connectionPreambleHelper = connectionPreambleHelper;
        private readonly ILogger<GatewayConnectionListener> _logger = logger;
        private readonly EndpointOptions _endpointOptions = endpointOptions.Value;
        private readonly OverloadDetector _overloadDetector = overloadDetector;
        private readonly Gateway _gateway = messageCenter.Gateway;

        protected override Connection CreateConnection(MessageTransport transport)
        {
            return new GatewayInboundConnection(
                transport,
                _gateway,
                _overloadDetector,
                _localSiloDetails,
                ConnectionOptions,
                _messageCenter,
                _connectionShared,
                _connectionPreambleHelper);
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            if (!HasListeners) return;

            lifecycle.Subscribe(nameof(GatewayConnectionListener), ServiceLifecycleStage.RuntimeInitialize - 1, this);
            lifecycle.Subscribe(nameof(GatewayConnectionListener), ServiceLifecycleStage.Active, _ => Task.Run(Start));
        }

        Task ILifecycleObserver.OnStart(CancellationToken ct) => Task.Run(() => BindAsync(ct));
        Task ILifecycleObserver.OnStop(CancellationToken ct) => Task.Run(() => StopAsync(ct));
    }
}
