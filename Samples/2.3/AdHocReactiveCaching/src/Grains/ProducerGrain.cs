using System;
using System.Threading.Tasks;
using Grains.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;
using Orleans.Configuration;

namespace Grains
{
    [Reentrant]
    public class ProducerGrain : Grain, IProducerGrain
    {
        private readonly ILogger<ProducerGrain> _logger;
        private readonly TimeSpan _waitTimeout;

        private IDisposable _timer;
        private VersionedValue<int> _state;
        private TaskCompletionSource<VersionedValue<int>> _wait;

        public ProducerGrain(ILogger<ProducerGrain> logger, IOptions<SiloMessagingOptions> messagingOptions)
        {
            _logger = logger;

            // this timeout helps resolve long polls gracefully just before orleans breaks them with a timeout exception
            // while not necessary for the reactive caching pattern to work
            // it avoid polluting the network and the logs with stack traces from timeout exceptions
            _waitTimeout = messagingOptions.Value.ResponseTimeout.Subtract(TimeSpan.FromSeconds(2));
        }

        private string GrainType => nameof(ProducerGrain);
        private string GrainKey => this.GetPrimaryKeyString();

        public override Task OnActivateAsync()
        {
            // initialize the state
            _state = VersionedValue<int>.None.NextVersion(0);

            // initialize the polling wait handle
            _wait = new TaskCompletionSource<VersionedValue<int>>();

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
        /// If no new data become available within the orleans response timeout minus some margin, then resolves with a "no data" response.
        /// Otherwise returns the current data immediately.
        /// </summary>
        public Task<VersionedValue<int>> LongPollAsync(VersionToken knownVersion) =>
            knownVersion == _state.Version
            ? _wait.Task.WithDefaultOnTimeout(_waitTimeout, VersionedValue<int>.None)
            : Task.FromResult(_state);
    }
}
