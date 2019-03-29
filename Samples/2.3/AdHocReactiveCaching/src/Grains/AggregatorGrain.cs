using System;
using System.Threading.Tasks;
using Grains.Models;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;

namespace Grains
{
    [Reentrant]
    public class AggregatorGrain : ReactiveGrain, IAggregatorGrain
    {
        private readonly ILogger<AggregatorGrain> _logger;
        private IProducerGrain _leftGrain;
        private IProducerGrain _rightGrain;
        private IDisposable _leftPoll;
        private IDisposable _rightPoll;
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

            // start long polling the left grain
            _leftPoll = RegisterReactivePoll(async () =>
            {
                _leftValue = await _leftGrain.LongPollAsync(_leftValue.Version);
                _logger.LogInformation(
                    "{@Time}: {@GrainType} {@GrainKey} updated left value to {@Value} with version {@Version}",
                    DateTime.Now.TimeOfDay, GrainType, GrainKey, _leftValue.Value, _leftValue.Version);
                await FulfillAsync();
            });

            // start long polling the right grain
            _rightPoll = RegisterReactivePoll(async () =>
            {
                _rightValue = await _rightGrain.LongPollAsync(_rightValue.Version);
                _logger.LogInformation(
                    "{@Time}: {@GrainType} {@GrainKey} updated right value to {@Value} with version {@Version}",
                    DateTime.Now.TimeOfDay, GrainType, GrainKey, _rightValue.Value, _rightValue.Version);
                await FulfillAsync();
            });

            await base.OnActivateAsync();
        }

        public Task<VersionedValue<int>> GetAsync() => Task.FromResult(_sumValue);

        public Task<VersionedValue<int>> LongPollAsync(int knownVersion) =>
            knownVersion == _sumValue.Version
            ? _wait.Task
            : Task.FromResult(_sumValue);

        private Task FulfillAsync()
        {
            _sumValue = _sumValue.NextVersion(_leftValue.Value + _rightValue.Value);
            _logger.LogInformation(
                    "{@Time}: {@GrainType} {@GrainKey} updated sum value to {@Value} with version {@Version}",
                    DateTime.Now.TimeOfDay, GrainType, GrainKey, _sumValue.Value, _sumValue.Version);
            _wait.SetResult(_sumValue);
            _wait = new TaskCompletionSource<VersionedValue<int>>();
            return Task.CompletedTask;
        }
    }
}
