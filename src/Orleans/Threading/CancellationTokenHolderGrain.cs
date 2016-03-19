using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Placement;
using Orleans.Runtime;

namespace Orleans.Threading
{
    [PreferLocalPlacement]
    internal class CancellationTokenHolderGrain : Grain, ICancellationTokenHolderGrain
    {
        private CancellationTokenSource _cancellationTokenSource;
        private bool _collectionScheduled;
        private TimeSpan _safetyCautionLimit = TimeSpan.FromSeconds(1);

        public override Task OnActivateAsync()
        {
            DelayDeactivation(TimeSpan.FromDays(5));
            _cancellationTokenSource = new CancellationTokenSource();
            return TaskDone.Done;
        }

        public Task<CancellationToken> GetCancellationToken()
        {
            return Task.FromResult(_cancellationTokenSource.Token);
        }

        public Task Cancel(TimeSpan deactivationDelay)
        {
            _cancellationTokenSource.Cancel();
            DelayDeactivation(deactivationDelay.Add(_safetyCautionLimit));
            _collectionScheduled = true;
            return TaskDone.Done;
        }

        public Task Dispose()
        {
            if (!_collectionScheduled)
            {
                DelayDeactivation(Constants.DEFAULT_CANCELLATION_TOKEN_HOLDER_DEACTIVATION_DELAY);
                _collectionScheduled = true;
            }

            return TaskDone.Done;
        }

        public override Task OnDeactivateAsync()
        {
            _cancellationTokenSource.Dispose();
            return base.OnDeactivateAsync();
        }
    }
}
