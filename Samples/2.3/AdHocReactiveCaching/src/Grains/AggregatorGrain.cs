using System;
using System.Threading.Tasks;
using Grains.Models;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;

namespace Grains
{
    [Reentrant]
    public class AggregatorGrain : Grain, IAggregatorGrain
    {
        private readonly ILogger<AggregatorGrain> _logger;
        private IProducerGrain _leftGrain;
        private IProducerGrain _rightGrain;
        private IDisposable _leftPollTimer;
        private IDisposable _rightPollTimer;
        private TaskCompletionSource<VersionedValue<int>> _wait = new TaskCompletionSource<VersionedValue<int>>();

        private VersionedValue<int> _leftValue = VersionedValue<int>.Default;
        private VersionedValue<int> _rightValue = VersionedValue<int>.Default;
        private VersionedValue<int> _sumValue = VersionedValue<int>.Default;

        public AggregatorGrain(ILogger<AggregatorGrain> logger)
        {
            _logger = logger;
        }

        private string GrainType => nameof(AggregatorGrain);
        private string GrainKey => this.GetPrimaryKeyString();

        public override async Task OnActivateAsync()
        {
            // derive the source grains to aggregate from the grain key
            var parts = GrainKey.Split('|');
            _leftGrain = GrainFactory.GetGrain<IProducerGrain>(parts[0]);
            _rightGrain = GrainFactory.GetGrain<IProducerGrain>(parts[1]);

            // get the starting values as they are now
            _leftValue = await _leftGrain.GetAsync();
            _rightValue = await _rightGrain.GetAsync();
            await FulfillAsync();

            // start long polling
            _leftPollTimer = RegisterTimer(_ => LongPollLeftAsync(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
            _rightPollTimer = RegisterTimer(_ => LongPollRightAsync(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(1));

            await base.OnActivateAsync();
        }

        public Task<VersionedValue<int>> GetAsync() => Task.FromResult(_sumValue);

        public Task<VersionedValue<int>> LongPollAsync(int knownVersion) =>
            knownVersion == _sumValue.Version
            ? _wait.Task
            : Task.FromResult(_sumValue);

        private async Task LongPollLeftAsync()
        {
            try
            {
                _leftValue = await _leftGrain.LongPollAsync(_leftValue.Version);
                await FulfillAsync();
            }
            catch (TimeoutException)
            {
            }
        }

        private async Task LongPollRightAsync()
        {
            try
            {
                _rightValue = await _rightGrain.LongPollAsync(_rightValue.Version);
                await FulfillAsync();
            }
            catch (TimeoutException)
            {
            }
        }

        private Task FulfillAsync()
        {
            _sumValue = _sumValue.NextVersion(_leftValue.Value + _rightValue.Value);
            _wait.SetResult(_sumValue);
            _wait = new TaskCompletionSource<VersionedValue<int>>();
            return Task.CompletedTask;
        }
    }
}
