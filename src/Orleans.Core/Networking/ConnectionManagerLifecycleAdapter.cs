using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Messaging
{
    internal class ConnectionManagerLifecycleAdapter<TLifecycle>
        : ILifecycleParticipant<TLifecycle>, ILifecycleObserver where TLifecycle : ILifecycleObservable
    {
        private readonly ConnectionManager connectionManager;

        public ConnectionManagerLifecycleAdapter(ConnectionManager connectionManager)
        {
            this.connectionManager = connectionManager;
        }

        public Task OnStart(CancellationToken ct) => Task.CompletedTask;

        public async Task OnStop(CancellationToken ct)
        {
            await Task.Run(() => this.connectionManager.Close(ct));
        }

        public void Participate(TLifecycle lifecycle)
        {
            lifecycle.Subscribe(
                nameof(ConnectionManager),
                ServiceLifecycleStage.RuntimeInitialize-1, // Components from RuntimeInitialize need network
                this);
        }
    }
}
