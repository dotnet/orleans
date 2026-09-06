using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Transactions.State
{
    [SuppressMessage(
        "Design",
        "CA1001:Types that own disposable fields should be disposable",
        Justification = "The cancellation source remains valid for all deactivation observers and is reclaimed with the grain activation.")]
    internal class ActivationLifetime : IActivationLifetime, ILifecycleObserver
    {
        private readonly CancellationTokenSource onDeactivating = new CancellationTokenSource();

        private int pendingDeactivationLocks;

        public ActivationLifetime(IGrainContext activationContext)
        {
            activationContext.ObservableLifecycle.Subscribe(GrainLifecycleStage.First, this);
            activationContext.ObservableLifecycle.Subscribe(GrainLifecycleStage.Last, this);
        }

        public CancellationToken OnDeactivating => this.onDeactivating.Token;

        public Task OnStart(CancellationToken ct) => Task.CompletedTask;

        public Task OnStop(CancellationToken ct)
        {
            this.onDeactivating.Cancel(throwOnFirstException: false);

            if (!ct.IsCancellationRequested && pendingDeactivationLocks > 0)
            {
                return OnStopAsync(ct);
            }

            return Task.CompletedTask;
        }

        private async Task OnStopAsync(CancellationToken ct)
        {
            var startTime = DateTime.UtcNow;
            var maxTime = TimeSpan.FromSeconds(5);
            while (!ct.IsCancellationRequested && pendingDeactivationLocks > 0 && DateTime.UtcNow - startTime < maxTime)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
            }
        }

        public IDisposable BlockDeactivation() => new BlockDeactivationDisposable(this);

        private class BlockDeactivationDisposable : IDisposable
        {
            private readonly ActivationLifetime owner;

            public BlockDeactivationDisposable(ActivationLifetime owner)
            {
                this.owner = owner;
                Interlocked.Increment(ref owner.pendingDeactivationLocks);
            }

            public void Dispose()
            {
                Interlocked.Decrement(ref owner.pendingDeactivationLocks);
            }
        }
    }
}
