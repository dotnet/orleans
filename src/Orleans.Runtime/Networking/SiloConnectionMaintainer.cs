using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Messaging
{
    internal class SiloConnectionMaintainer : ILifecycleParticipant<ISiloLifecycle>, ISiloStatusListener, ILifecycleObserver
    {
        private readonly ConnectionManager connectionManager;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly ILogger<SiloConnectionMaintainer> log;

        public SiloConnectionMaintainer(
            ConnectionManager connectionManager,
            ISiloStatusOracle siloStatusOracle,
            ILogger<SiloConnectionMaintainer> log)
        {
            this.connectionManager = connectionManager;
            this.siloStatusOracle = siloStatusOracle;
            this.log = log;
        }

        public Task OnStart(CancellationToken ct)
        {
            this.siloStatusOracle.SubscribeToSiloStatusEvents(this);
            return Task.CompletedTask;
        }

        public Task OnStop(CancellationToken ct)
        {
            this.siloStatusOracle.UnSubscribeFromSiloStatusEvents(this);
            return Task.CompletedTask;
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(nameof(SiloConnectionMaintainer), ServiceLifecycleStage.RuntimeInitialize, this);
        }

        public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            if (status == SiloStatus.Dead && updatedSilo != siloStatusOracle.SiloAddress)
            {
                _ = Task.Run(() => this.CloseConnectionAsync(updatedSilo));
            }
        }

        private async Task CloseConnectionAsync(SiloAddress silo)
        {
            try
            {
                // Allow a short grace period to complete sending pending messages (eg, gossip responses)
                await Task.Delay(TimeSpan.FromSeconds(10));

                await this.connectionManager.CloseAsync(silo);
            }
            catch (Exception exception)
            {
                this.log.LogInformation(exception, "Exception while closing connections to defunct silo {SiloAddress}", silo);
            }
        }
    }
}