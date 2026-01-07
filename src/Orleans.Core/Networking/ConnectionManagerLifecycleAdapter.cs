using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Messaging
{
    internal class ConnectionManagerLifecycleAdapter<TLifecycle>(ConnectionManager connectionManager)
        : ILifecycleParticipant<TLifecycle>, ILifecycleObserver where TLifecycle : ILifecycleObservable
    {
        public Task OnStart(CancellationToken ct) => Task.CompletedTask;

        public async Task OnStop(CancellationToken ct)
        {
            await Task.Run(() => connectionManager.Close(ct));
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
