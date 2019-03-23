using System;
using System.Threading.Tasks;
using Grains.Models;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;

namespace Grains
{
    [Reentrant]
    public class ProducerGrain : Grain, IProducerGrain
    {
        private readonly ILogger<ProducerGrain> _logger;
        private IDisposable _timer;
        private VersionedValue<int> _state;
        private TaskCompletionSource<VersionedValue<int>> _wait;

        public ProducerGrain(ILogger<ProducerGrain> logger)
        {
            _logger = logger;
        }

        private string GrainType => nameof(ProducerGrain);
        private string GrainKey => this.GetPrimaryKeyString();

        public override Task OnActivateAsync()
        {
            // initialize the state
            _state = new VersionedValue<int>(0, 0);

            // initialize the polling wait handle
            _wait = new TaskCompletionSource<VersionedValue<int>>();
            _wait.SetResult(_state);

            return base.OnActivateAsync();
        }

        public Task StartAsync(int increment, TimeSpan delay)
        {
            // start or restart the data generation timer
            _timer?.Dispose();
            _timer = RegisterTimer(_ => IncrementAsync(increment), null, delay, delay);
            return Task.CompletedTask;
        }

        private Task IncrementAsync(int increment)
        {
            // update the state
            _state = _state.NextVersion(_state.Value + increment);
            _logger.LogInformation("{@GrainType} {@GrainKey} updated value to {@Value} with version {@Version}",
                GrainType, GrainKey, _state.Value, _state.Version);

            // fulfill waiting promises
            _wait.SetResult(_state);
            _wait = new TaskCompletionSource<VersionedValue<int>>();

            return Task.CompletedTask;
        }

        /// <summary>
        /// Returns the current state without polling.
        /// </summary>
        public Task<VersionedValue<int>> GetAsync() => Task.FromResult(_state);

        /// <summary>
        /// If the given version is the same as the current version then resolves when a new version of data is available.
        /// Otherwise returns the current data immediately.
        /// </summary>
        public Task<VersionedValue<int>> PollAsync(int knownVersion) =>
            knownVersion == _state.Version
            ? _wait.Task
            : Task.FromResult(_state);
    }
}
