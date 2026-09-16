using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Internal;
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

        private readonly AdmissionGate deactivationGate = new();
        private readonly TimeProvider timeProvider;

        public ActivationLifetime(IGrainContext activationContext)
            : this(activationContext.ObservableLifecycle, TimeProvider.System)
        {
        }

        internal ActivationLifetime(IGrainLifecycle lifecycle, TimeProvider timeProvider)
        {
            this.timeProvider = timeProvider;
            lifecycle.Subscribe(GrainLifecycleStage.First, this);
            lifecycle.Subscribe(GrainLifecycleStage.Last, this);
        }

        public CancellationToken OnDeactivating => this.onDeactivating.Token;

        public Task OnStart(CancellationToken ct) => Task.CompletedTask;

        public Task OnStop(CancellationToken ct)
        {
            var drained = this.deactivationGate.CloseAsync();
            this.onDeactivating.Cancel(throwOnFirstException: false);

            if (!ct.IsCancellationRequested && !drained.IsCompleted)
            {
                return OnStopAsync(drained, ct);
            }

            return Task.CompletedTask;
        }

        private async Task OnStopAsync(Task drained, CancellationToken ct)
        {
            try
            {
                await drained.WaitAsync(TimeSpan.FromSeconds(5), this.timeProvider, ct);
            }
            catch (TimeoutException)
            {
                // Deactivation proceeds after the best-effort drain budget expires.
            }
        }

        public AdmissionGate.Admission TryBlockDeactivation() => this.deactivationGate.TryEnter();
    }
}
