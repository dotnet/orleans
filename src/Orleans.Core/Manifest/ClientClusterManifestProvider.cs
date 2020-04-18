using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Messaging;
using Orleans.Metadata;
using Orleans.Runtime.Utilities;

namespace Orleans.Runtime
{
    internal class ClientClusterManifestProvider : IClusterManifestProvider, IAsyncDisposable
    {
        private readonly TaskCompletionSource<bool> _initialized = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IInternalGrainFactory _grainFactory;
        private readonly ILogger<ClientClusterManifestProvider> _logger;
        private readonly GatewayManager _gatewayManager;
        private readonly TypeManagementOptions _options;
        private readonly AsyncEnumerable<ClusterManifest> _updates;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private ClusterManifest _current;
        private Task _runTask;

        public ClientClusterManifestProvider(
            IInternalGrainFactory grainFactory,
            GatewayManager gatewayManager,
            ILogger<ClientClusterManifestProvider> logger,
            IOptions<TypeManagementOptions> options)
        {
            _grainFactory = grainFactory;
            _logger = logger;
            _gatewayManager = gatewayManager;
            _options = options.Value;
            _updates = new AsyncEnumerable<ClusterManifest>(
                (previous, proposed) => previous is null || proposed.Version == MajorMinorVersion.Zero || proposed.Version > previous.Version,
                _current)
            {
                OnPublished = update => Interlocked.Exchange(ref _current, update)
            };
        }

        public ClusterManifest Current => _current;

        public IAsyncEnumerable<ClusterManifest> Updates => _updates;

        public Task StartAsync()
        {
            _runTask = Task.Run(RunAsync);
            return _initialized.Task;
        }

        private async Task RunAsync()
        {
            try
            {
                var cancellationTask = _cancellation.Token.WhenCancelled();
                while (!_cancellation.IsCancellationRequested)
                {
                    var gateway = _gatewayManager.GetLiveGateway();
                    try
                    {
                        var provider = _grainFactory.GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, gateway);
                        var refreshTask = provider.GetClusterManifest().AsTask();
                        var task = await Task.WhenAny(cancellationTask, refreshTask);

                        if (ReferenceEquals(task, cancellationTask))
                        {
                            return;
                        }

                        if (!_updates.TryPublish(await refreshTask))
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(500));
                            continue;
                        }

                        _initialized.TrySetResult(true);

                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug("Refreshed cluster manifest");
                        }

                        await Task.Delay(_options.TypeMapRefreshInterval);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(exception, "Error trying to get cluster manifest from gateway {Gateway}", gateway);
                        await Task.Delay(TimeSpan.FromSeconds(5));
                    }
                }
            }
            finally
            {
                _initialized.TrySetResult(false);

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Stopped refreshing cluster manifest");
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            return _runTask is Task task ? new ValueTask(task) : default;
        }
    }
}
