using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Runtime.Hosting.Clustering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Hosting.Clustering
{
    /// <summary>
    /// Reflects cluster configuration changes between Orleans and a cluster like kubernetes.
    /// </summary>
    public sealed class ClusterAgent : ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly IOptionsMonitor<ClusterMonitoringOptions> _options;
        private readonly IClusterProvider _clusterProvider;
        private readonly IClusterMembershipService _clusterMembershipService;
        private readonly ILocalSiloDetails _localSiloDetails;
        private readonly ILogger<ClusterAgent> _logger;
        private readonly CancellationTokenSource _shutdownToken;
        private readonly SemaphoreSlim _pauseMonitoringSemaphore = new SemaphoreSlim(0);
        private volatile bool _enableMonitoring;
        private Task _runTask;

        public ClusterAgent(
            IClusterMembershipService clusterMembershipService,
            ILogger<ClusterAgent> logger,
            IOptionsMonitor<ClusterMonitoringOptions> options,
            IClusterProvider clusterProvider,
            ILocalSiloDetails localSiloDetails)
        {
            _localSiloDetails = localSiloDetails;
            _logger = logger;
            _shutdownToken = new CancellationTokenSource();
            _options = options;
            _clusterProvider = clusterProvider;
            _clusterMembershipService = clusterMembershipService;
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(
                nameof(ClusterAgent),
                ServiceLifecycleStage.AfterRuntimeGrainServices,
                OnRuntimeInitializeStart,
                OnRuntimeInitializeStop);
        }

        private async Task OnRuntimeInitializeStart(CancellationToken cancellation)
        {
            // Find the currently known cluster members first, before interrogating Kubernetes
            await _clusterMembershipService.Refresh();
            var snapshot = _clusterMembershipService.CurrentSnapshot.Members;

            // Find the pods which correspond to this cluster
            var members = await _clusterProvider.ListMembersAsync(cancellation);
            var memberNames = members.Select(x => x.Name);

            HashSet<string> known = new HashSet<string>();
            var knownMap = new Dictionary<string, ClusterMember>();

            foreach (var member in members)
            {
                if (member.IsCurrentSilo)
                {
                    known.Add(member.Name);
                }
            }
            foreach (var member in snapshot.Values)
            {
                if (member.Status == SiloStatus.Dead)
                {
                    continue;
                }

                known.Add(member.Name);
                knownMap[member.Name] = member;
            }

            var unknown = new List<string>(memberNames.Except(known));
            unknown.Sort();
            foreach (var pod in unknown)
            {
                _logger.LogWarning("Pod {PodName} does not correspond to any known silos", pod);

                // Delete the pod once it has been active long enough?
            }

            var unmatched = new List<string>(known.Except(memberNames));
            unmatched.Sort();
            foreach (var pod in unmatched)
            {
                var siloAddress = knownMap[pod];
                _logger.LogWarning("Silo {SiloAddress} does not correspond to any known pod. Marking it as dead.", siloAddress);
                await _clusterMembershipService.TryKill(siloAddress.SiloAddress);
            }

            // Start monitoring loop
            ThreadPool.UnsafeQueueUserWorkItem(_ => _runTask = Task.WhenAll(Task.Run(MonitorOrleansClustering), Task.Run(MonitorKubernetesPods)), null);
        }

        public async Task OnRuntimeInitializeStop(CancellationToken cancellationToken)
        {
            _shutdownToken.Cancel();
            _enableMonitoring = false;
            _pauseMonitoringSemaphore.Release();

            if (_runTask is object)
            {
                await Task.WhenAny(_runTask, Task.Delay(TimeSpan.FromMinutes(1), cancellationToken));
            }
        }

        private async Task MonitorOrleansClustering()
        {
            var previous = _clusterMembershipService.CurrentSnapshot;
            while (!_shutdownToken.IsCancellationRequested)
            {
                try
                {
                    await foreach (var update in _clusterMembershipService.MembershipUpdates.WithCancellation(_shutdownToken.Token))
                    {
                        // Determine which silos should be monitoring Kubernetes
                        var chosenSilos = _clusterMembershipService.CurrentSnapshot.Members.Values
                            .Where(s => s.Status == SiloStatus.Active)
                            .OrderBy(s => s.SiloAddress)
                            .Take(_options.CurrentValue.MaxAgents)
                            .ToList();

                        if (!_enableMonitoring && chosenSilos.Any(s => s.SiloAddress.Equals(_localSiloDetails.SiloAddress)))
                        {
                            _enableMonitoring = true;
                            _pauseMonitoringSemaphore.Release(1);
                        }
                        else if (_enableMonitoring)
                        {
                            _enableMonitoring = false;
                        }

                        if (_enableMonitoring && _options.CurrentValue.DeleteDefunctSiloPods)
                        {
                            var delta = update.CreateUpdate(previous);
                            foreach (var change in delta.Changes)
                            {
                                if (change.SiloAddress.Equals(_localSiloDetails.SiloAddress))
                                {
                                    // Ignore all changes for this silo
                                    continue;
                                }

                                if (change.Status == SiloStatus.Dead)
                                {
                                    var member = _clusterProvider.Describe(change.Name);
                                    try
                                    {
                                        if (_logger.IsEnabled(LogLevel.Information))
                                        {
                                            _logger.LogInformation("Silo {SiloAddress} is dead, proceeding to delete the corresponding '{member'}", member);
                                        }

                                        await _clusterProvider.DeleteAsync(change.Name);
                                    }
                                    catch (Exception exception)
                                    {
                                        _logger.LogError(exception, "Error deleting '{member}' corresponding to defunct silo {SiloAddress}", member, change.SiloAddress);
                                    }
                                }
                            }
                        }

                        previous = update;
                    }
                }
                catch (Exception exception) when (!(_shutdownToken.IsCancellationRequested && (exception is TaskCanceledException || exception is OperationCanceledException)))
                {
                    _logger.LogError(exception, "Error monitoring cluster changes");
                    if (!_shutdownToken.IsCancellationRequested)
                    {
                        await Task.Delay(5000);
                    }
                }
            }
        }

        private async Task MonitorKubernetesPods()
        {
            while (!_shutdownToken.IsCancellationRequested)
            {
                try
                {
                    if (!_enableMonitoring)
                    {
                        // Pulse the semaphore to avoid spinning in a tight loop.
                        await _pauseMonitoringSemaphore.WaitAsync();
                        continue;
                    }

                    if (_shutdownToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await foreach (var @event in _clusterProvider.MonitorChangesAsync(_shutdownToken.Token))
                    {
                        if (!_enableMonitoring || _shutdownToken.IsCancellationRequested)
                        {
                            break;
                        }

                        if (@event.Member.IsCurrentSilo)
                        {
                            // Never declare ourselves dead this way.
                            continue;
                        }

                        if (@event is ClusterMemberDeleted)
                        {
                            if (this.TryMatchSilo(@event.Member, out var member) && member.Status != SiloStatus.Dead)
                            {
                                if (_logger.IsEnabled(LogLevel.Information))
                                {
                                    _logger.LogInformation("Declaring server {Silo} dead since its corresponding pod, {Pod}, has been deleted", member.SiloAddress, @event.Member.Name);
                                }

                                await _clusterMembershipService.TryKill(member.SiloAddress);
                            }
                        }
                    }

                    if (_enableMonitoring && !_shutdownToken.IsCancellationRequested)
                    {
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug("Unexpected end of stream from Kubernetes API. Will try again.");
                        }

                        await Task.Delay(5000);
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Error monitoring Kubernetes pods");
                    if (!_shutdownToken.IsCancellationRequested)
                    {
                        await Task.Delay(5000);
                    }
                }
            }
        }

        private bool TryMatchSilo(ExternalClusterMember clusterMember, out ClusterMember server)
        {
            var snapshot = _clusterMembershipService.CurrentSnapshot;
            foreach (var member in snapshot.Members)
            {
                if (string.Equals(member.Value.Name, clusterMember.Name, StringComparison.Ordinal))
                {
                    server = member.Value;
                    return true;
                }
            }

            server = default;
            return false;
        }
    }
}
