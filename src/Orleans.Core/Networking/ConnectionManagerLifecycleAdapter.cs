using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Messaging
{
    internal class ConnectionManagerLifecycleAdapter<TLifecycle>
        : ILifecycleParticipant<TLifecycle>, ILifecycleObserver where TLifecycle : ILifecycleObservable
    {
        private readonly ConnectionManager connectionManager;
        private readonly Func<CancellationToken, Task>? _beforeClose;

        public ConnectionManagerLifecycleAdapter(ConnectionManager connectionManager, Func<CancellationToken, Task>? beforeClose = null)
        {
            this.connectionManager = connectionManager;
            _beforeClose = beforeClose;
        }

        public Task OnStart(CancellationToken ct) => Task.CompletedTask;

        public async Task OnStop(CancellationToken ct)
        {
            try
            {
                if (_beforeClose is { } beforeClose)
                {
                    await beforeClose(ct).ConfigureAwait(false);
                }
            }
            finally
            {
                await Task.Run(() => this.connectionManager.Close(ct), CancellationToken.None).ConfigureAwait(false);
            }
        }

        public void Participate(TLifecycle lifecycle)
        {
            lifecycle.Subscribe(
                nameof(ConnectionManager),
                ServiceLifecycleStage.RuntimeInitialize - 1, // Components from RuntimeInitialize need network
                this);
        }
    }
}
