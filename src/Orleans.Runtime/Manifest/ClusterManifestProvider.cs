using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Metadata;
using Orleans.Runtime.Providers;
using Orleans.Runtime.Utilities;

namespace Orleans.Runtime.Metadata
{
    internal class ClusterManifestProvider : IClusterManifestProvider, IAsyncDisposable, IDisposable, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly SiloAddress _localSiloAddress;
        private readonly ILogger<ClusterManifestProvider> _logger;
        private readonly IServiceProvider _services;
        private readonly IClusterMembershipService _clusterMembershipService;
        private readonly IFatalErrorHandler _fatalErrorHandler;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly AsyncEnumerable<ClusterManifest> _updates;
        private ClusterManifest _current;
        private Task _runTask;

        public ClusterManifestProvider(
            ILocalSiloDetails localSiloDetails,
            SiloManifestProvider siloManifestProvider,
            ClusterMembershipService clusterMembershipService,
            IFatalErrorHandler fatalErrorHandler,
            ILogger<ClusterManifestProvider> logger,
            IServiceProvider services)
        {
            _localSiloAddress = localSiloDetails.SiloAddress;
            _logger = logger;
            _services = services;
            _clusterMembershipService = clusterMembershipService;
            _fatalErrorHandler = fatalErrorHandler;
            this.LocalGrainManifest = siloManifestProvider.SiloManifest;
            _current = new ClusterManifest(
                MajorMinorVersion.Zero,
                ImmutableDictionary.CreateRange(new[] { new KeyValuePair<SiloAddress, GrainManifest>(localSiloDetails.SiloAddress, this.LocalGrainManifest) }),
                ImmutableArray.Create(this.LocalGrainManifest));
            _updates = new AsyncEnumerable<ClusterManifest>(
                (previous, proposed) => previous.Version <= MajorMinorVersion.Zero || proposed.Version > previous.Version,
                _current)
            {
                OnPublished = update => Interlocked.Exchange(ref _current, update)
            };
        }

        public ClusterManifest Current => _current;

        public IAsyncEnumerable<ClusterManifest> Updates => _updates;

        public GrainManifest LocalGrainManifest { get; }

        private async Task ProcessMembershipUpdates()
        {
            try
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Starting to process membership updates");
                }

                var cancellation = _cancellation.Token;
                await foreach (var _ in _clusterMembershipService.MembershipUpdates.WithCancellation(cancellation))
                {
                    while (true)
                    {
                        var membershipSnapshot = _clusterMembershipService.CurrentSnapshot;

                        var success = await this.UpdateManifest(membershipSnapshot);

                        if (success || cancellation.IsCancellationRequested)
                        {
                            break;
                        }

                        await Task.Delay(TimeSpan.FromSeconds(5));
                    }
                }
            }
            catch (Exception exception) when (_fatalErrorHandler.IsUnexpected(exception))
            {
                _fatalErrorHandler.OnFatalException(this, nameof(ProcessMembershipUpdates), exception);
            }
            finally
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Stopped processing membership updates");
                }
            }
        }

        private async Task<bool> UpdateManifest(ClusterMembershipSnapshot clusterMembership)
        {
            var existingManifest = _current;
            var builder = existingManifest.Silos.ToBuilder();
            var modified = false;

            // First, remove defunct entries.
            foreach (var entry in existingManifest.Silos)
            {
                var address = entry.Key;
                var status = clusterMembership.GetSiloStatus(address);

                if (address.Equals(_localSiloAddress))
                {
                    // The local silo is always present in the manifest.
                    continue;
                }

                if (status == SiloStatus.None || status == SiloStatus.Dead)
                {
                    builder.Remove(address);
                    modified = true;
                }
            }

            // Next, fill missing entries.
            var tasks = new List<Task<(SiloAddress Key, GrainManifest Value, Exception Exception)>>();
            foreach (var entry in clusterMembership.Members)
            {
                var member = entry.Value;

                if (member.SiloAddress.Equals(_localSiloAddress))
                {
                    // The local silo is always present in the manifest.
                    continue;
                }

                if (existingManifest.Silos.ContainsKey(member.SiloAddress))
                {
                    // Manifest has already been retrieved for the cluster member.
                    continue;
                }

                if (member.Status != SiloStatus.Active)
                {
                    // If the member is not yet active, it may not be ready to process requests.
                    continue;
                }

                tasks.Add(GetManifest(member.SiloAddress));

                async Task<(SiloAddress, GrainManifest, Exception)> GetManifest(SiloAddress siloAddress)
                {
                    try
                    {
                        // Get the manifest from the remote silo.
                        var grainFactory = _services.GetRequiredService<IInternalGrainFactory>();
                        var remoteManifestProvider = grainFactory.GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, member.SiloAddress);
                        var manifest = await remoteManifestProvider.GetSiloManifest();
                        return (siloAddress, manifest, null);
                    }
                    catch (Exception exception)
                    {
                        return (siloAddress, null, exception);
                    }
                }
            }

            var fetchSuccess = true;
            await Task.WhenAll(tasks);
            foreach (var task in tasks)
            {
                var result = await task;
                if (result.Exception is Exception exception)
                {
                    fetchSuccess = false;
                    _logger.LogWarning(exception, "Error retrieving silo manifest for silo {SiloAddress}", result.Key);
                }
                else
                {
                    modified = true;
                    builder[result.Key] = result.Value;
                }
            }

            // Regardless of success or failure, update the manifest if it has been modified.
            var version = new MajorMinorVersion(clusterMembership.Version.Value, existingManifest.Version.Minor + 1);
            if (modified)
            {
                return _updates.TryPublish(new ClusterManifest(version, builder.ToImmutable(), builder.Values.ToImmutableArray())) && fetchSuccess;
            }

            return fetchSuccess;
        }

        private Task StartAsync(CancellationToken _)
        {
            _runTask = Task.Run(ProcessMembershipUpdates);
            return Task.CompletedTask;
        }

        private Task Initialize(CancellationToken _)
        {
            var catalog = _services.GetRequiredService<Catalog>();
            catalog.RegisterSystemTarget(ActivatorUtilities.CreateInstance<ClusterManifestSystemTarget>(_services));
            return Task.CompletedTask;
        }

        private async Task StopAsync(CancellationToken cancellationToken)
        {
            _cancellation.Cancel();
            if (_runTask is Task task)
            {
                await task;
            }
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(
                nameof(ClusterManifestProvider),
                ServiceLifecycleStage.RuntimeServices,
                Initialize,
                _ => Task.CompletedTask);

            lifecycle.Subscribe(
                nameof(ClusterManifestProvider),
                ServiceLifecycleStage.RuntimeGrainServices,
                StartAsync,
                StopAsync);
        }

        public async ValueTask DisposeAsync()
        {
            await this.StopAsync(CancellationToken.None);
        }

        public void Dispose()
        {
            _cancellation.Cancel();
        }
    }
}
