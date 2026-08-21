using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Runtime.Hosting.Clustering;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Hosting.Clustering
{
    /// <summary>
    /// Reconciles Orleans cluster membership with an external cluster provider.
    /// </summary>
    public sealed partial class ClusterAgent : ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly IOptionsMonitor<ClusterMonitoringOptions> _options;
        private readonly IClusterProvider _clusterProvider;
        private readonly IClusterMembershipService _clusterMembershipService;
        private readonly ILocalSiloDetails _localSiloDetails;
        private readonly ILogger<ClusterAgent> _logger;
        private readonly CancellationTokenSource _shutdownToken = new();
        private readonly SemaphoreSlim _pauseMonitoringSemaphore = new(0);
        private volatile bool _enableMonitoring;
        private Task _runTask = null!;

        public ClusterAgent(
            IClusterMembershipService clusterMembershipService,
            ILogger<ClusterAgent> logger,
            IOptionsMonitor<ClusterMonitoringOptions> options,
            IClusterProvider clusterProvider,
            ILocalSiloDetails localSiloDetails)
        {
            _logger = logger;
            _options = options;
            _clusterProvider = clusterProvider;
            _clusterMembershipService = clusterMembershipService;
            _localSiloDetails = localSiloDetails;
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(
                nameof(ClusterAgent),
                ServiceLifecycleStage.AfterRuntimeGrainServices,
                OnStart,
                OnStop);
        }

        private async Task OnStart(CancellationToken cancellation)
        {
            var attempts = 0;
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    await _clusterMembershipService.Refresh();
                    var snapshot = _clusterMembershipService.CurrentSnapshot.Members;
                    var externalMembers = (await _clusterProvider.ListMembersAsync(cancellation)).ToArray();
                    var externalMemberNames = externalMembers.Select(member => member.Name).ToHashSet(StringComparer.Ordinal);
                    var knownMemberNames = new HashSet<string>(StringComparer.Ordinal);
                    var knownMembers = new Dictionary<string, ClusterMember>(StringComparer.Ordinal);

                    foreach (var member in externalMembers)
                    {
                        if (member.IsCurrentSilo)
                        {
                            knownMemberNames.Add(member.Name);
                        }
                    }

                    foreach (var member in snapshot.Values)
                    {
                        if (member.Status == SiloStatus.Dead)
                        {
                            continue;
                        }

                        knownMemberNames.Add(member.Name);
                        knownMembers[member.Name] = member;
                    }

                    var unknownMembers = new List<string>(externalMemberNames.Except(knownMemberNames));
                    unknownMembers.Sort(StringComparer.Ordinal);
                    foreach (var memberName in unknownMembers)
                    {
                        LogWarningUnknownExternalMember(_clusterProvider.Describe(memberName));
                    }

                    var unmatchedMembers = new List<string>(knownMemberNames.Except(externalMemberNames));
                    unmatchedMembers.Sort(StringComparer.Ordinal);
                    foreach (var memberName in unmatchedMembers)
                    {
                        var member = knownMembers[memberName];
                        if (member.Status is not SiloStatus.Active)
                        {
                            continue;
                        }

                        LogWarningSiloWithoutExternalMember(member, _clusterProvider.Describe(memberName));
                        await _clusterMembershipService.TryKill(member.SiloAddress);
                    }

                    break;
                }
                catch (Exception exception)
                {
                    LogErrorInitializing(exception);
                    if (++attempts > _options.CurrentValue.MaxInitializationAttempts)
                    {
                        throw;
                    }

                    await Task.Delay(1000, cancellation);
                }
            }

            ThreadPool.UnsafeQueueUserWorkItem(
                _ => _runTask = Task.WhenAll(Task.Run(MonitorOrleansClustering), Task.Run(MonitorExternalCluster)),
                null);
        }

        public async Task OnStop(CancellationToken cancellationToken)
        {
            _shutdownToken.Cancel();
            _enableMonitoring = false;
            _pauseMonitoringSemaphore.Release();

            if (_runTask is not null)
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
                        var chosenSilos = _clusterMembershipService.CurrentSnapshot.Members.Values
                            .Where(silo => silo.Status == SiloStatus.Active)
                            .OrderBy(silo => silo.SiloAddress)
                            .Take(_options.CurrentValue.MaxAgents)
                            .ToList();
                        var shouldMonitor = chosenSilos.Exists(silo => silo.SiloAddress.Equals(_localSiloDetails.SiloAddress));

                        if (shouldMonitor && !_enableMonitoring)
                        {
                            _enableMonitoring = true;
                            _pauseMonitoringSemaphore.Release(1);
                        }
                        else if (!shouldMonitor && _enableMonitoring)
                        {
                            _enableMonitoring = false;
                        }

                        if (_enableMonitoring && _options.CurrentValue.DeleteDefunctClusterMembers)
                        {
                            var delta = update.CreateUpdate(previous);
                            foreach (var change in delta.Changes)
                            {
                                if (change.SiloAddress.Equals(_localSiloDetails.SiloAddress))
                                {
                                    continue;
                                }

                                if (change.Status == SiloStatus.Dead)
                                {
                                    var description = _clusterProvider.Describe(change.Name);
                                    try
                                    {
                                        LogInformationDeletingDeadSiloMember(change.SiloAddress, description);
                                        await _clusterProvider.DeleteAsync(change.Name, _shutdownToken.Token);
                                    }
                                    catch (Exception exception)
                                    {
                                        LogErrorDeletingExternalMember(exception, description, change.SiloAddress);
                                    }
                                }
                            }
                        }

                        previous = update;
                    }
                }
                catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    LogDebugErrorMonitoringCluster(exception);

                    if (!_shutdownToken.IsCancellationRequested)
                    {
                        await Task.Delay(5000);
                    }
                }
            }
        }

        private async Task MonitorExternalCluster()
        {
            while (!_shutdownToken.IsCancellationRequested)
            {
                try
                {
                    if (!_enableMonitoring)
                    {
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
                            continue;
                        }

                        if (@event is ClusterMemberDeleted
                            && TryMatchSilo(@event.Member, out var member)
                            && member.Status != SiloStatus.Dead)
                        {
                            LogInformationDeclaringServerDead(member.SiloAddress, @event.Member.Description);
                            await _clusterMembershipService.TryKill(member.SiloAddress);
                        }
                    }

                    if (_enableMonitoring && !_shutdownToken.IsCancellationRequested)
                    {
                        LogDebugUnexpectedEndOfStream();
                        await Task.Delay(5000);
                    }
                }
                catch (Exception exception) when (!(_shutdownToken.IsCancellationRequested && exception is OperationCanceledException))
                {
                    LogErrorMonitoringExternalCluster(exception);
                    if (!_shutdownToken.IsCancellationRequested)
                    {
                        await Task.Delay(5000);
                    }
                }
            }
        }

        private bool TryMatchSilo(ExternalClusterMember clusterMember, [NotNullWhen(true)] out ClusterMember? server)
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

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "External cluster member {Member} does not correspond to any known silo"
        )]
        private partial void LogWarningUnknownExternalMember(string member);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Silo {SiloAddress} does not correspond to external cluster member {Member}. Marking it as dead."
        )]
        private partial void LogWarningSiloWithoutExternalMember(ClusterMember siloAddress, string member);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error while initializing the external cluster agent"
        )]
        private partial void LogErrorInitializing(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Silo {SiloAddress} is dead, proceeding to delete external cluster member {Member}"
        )]
        private partial void LogInformationDeletingDeadSiloMember(SiloAddress siloAddress, string member);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error deleting external cluster member {Member} corresponding to defunct silo {SiloAddress}"
        )]
        private partial void LogErrorDeletingExternalMember(Exception exception, string member, SiloAddress siloAddress);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Error while monitoring Orleans cluster changes"
        )]
        private partial void LogDebugErrorMonitoringCluster(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "The external cluster provider event stream ended. The cluster agent will reconnect."
        )]
        private partial void LogDebugUnexpectedEndOfStream();

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error monitoring the external cluster provider"
        )]
        private partial void LogErrorMonitoringExternalCluster(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Declaring server {Silo} dead since external cluster member {Member} was deleted"
        )]
        private partial void LogInformationDeclaringServerDead(SiloAddress silo, string member);
    }
}
