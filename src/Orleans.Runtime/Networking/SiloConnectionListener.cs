using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Connections.Transport;

#nullable disable
namespace Orleans.Runtime.Messaging
{
    internal sealed class SiloConnectionListener(
        IEnumerable<MessageTransportListener> listeners,
        IEnumerable<IMessageTransportListenerMiddleware> listenerMiddleware,
        IOptions<ConnectionOptions> connectionOptions,
        MessageCenter messageCenter,
        IOptions<EndpointOptions> endpointOptions,
        ILocalSiloDetails localSiloDetails,
        ConnectionManager connectionManager,
        ConnectionCommon connectionShared,
        ProbeRequestMonitor probeRequestMonitor,
        ConnectionPreambleHelper connectionPreambleHelper) : ConnectionListener(
              listeners.Where(static listener => listener.ListenerName.Equals(DefaultListenerName, StringComparison.Ordinal)),
              listenerMiddleware,
              connectionOptions,
              connectionManager,
              connectionShared), ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver
    {
        public const string DefaultListenerName = "silo";
        private readonly ILocalSiloDetails _localSiloDetails = localSiloDetails;
        private readonly MessageCenter _messageCenter = messageCenter;
        private readonly EndpointOptions _endpointOptions = endpointOptions.Value;
        private readonly ConnectionManager _connectionManager = connectionManager;
        private readonly ConnectionCommon _connectionShared = connectionShared;
        private readonly ProbeRequestMonitor _probeRequestMonitor = probeRequestMonitor;
        private readonly ConnectionPreambleHelper _connectionPreambleHelper = connectionPreambleHelper;

        protected override Connection CreateConnection(MessageTransport transport)
        {
            return new SiloConnection(
                default,
                transport,
                _messageCenter,
                _localSiloDetails,
                _connectionManager,
                ConnectionOptions,
                _connectionShared,
                _probeRequestMonitor,
                _connectionPreambleHelper);
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            if (!HasListeners) return;

            lifecycle.Subscribe(nameof(SiloConnectionListener), ServiceLifecycleStage.RuntimeInitialize - 1, this);
        }

        Task ILifecycleObserver.OnStart(CancellationToken ct) => Task.Run(async () =>
        {
            await BindAsync(ct);

            // Start accepting connections immediately.
            Start();
        });

        Task ILifecycleObserver.OnStop(CancellationToken ct) => Task.Run(() => StopAsync(ct));
    }
}
