using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

#nullable enable
namespace Orleans.Runtime.MembershipService.SiloMetadata;

internal partial class SiloMetadataCache(
    ISiloMetadataClient siloMetadataClient,
    MembershipTableManager membershipTableManager,
    IOptions<ClusterMembershipOptions> clusterMembershipOptions,
    ILogger<SiloMetadataCache> logger)
    : ISiloMetadataCache, ILifecycleParticipant<ISiloLifecycle>, IDisposable
{
    private readonly ConcurrentDictionary<SiloAddress, SiloMetadata> _metadata = new();
    private readonly Dictionary<SiloAddress, DateTime> _negativeCache = new();
    private readonly CancellationTokenSource _cts = new();
    private TimeSpan negativeCachePeriod;

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
        Task? task = null;
        Task OnStart(CancellationToken _)
        {
            // This gives time for the cluster to be voted Dead and for membership updates to propagate that out
            negativeCachePeriod = clusterMembershipOptions.Value.ProbeTimeout * clusterMembershipOptions.Value.NumMissedProbesLimit
                                  + (2 * clusterMembershipOptions.Value.TableRefreshTimeout);
            task = Task.Run(() => this.ProcessMembershipUpdates(_cts.Token));
            return Task.CompletedTask;
        }

        async Task OnStop(CancellationToken ct)
        {
            await _cts.CancelAsync().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (task is not null)
            {
                await task.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        lifecycle.Subscribe(
            nameof(ClusterMembershipService),
            ServiceLifecycleStage.RuntimeServices,
            OnStart,
            OnStop);
    }

    private async Task ProcessMembershipUpdates(CancellationToken ct)
    {
        try
        {
            LogDebugStartProcessingMembershipUpdates(logger);
            await foreach (var update in membershipTableManager.MembershipTableUpdates.WithCancellation(ct))
            {
                // Add entries for members that aren't already in the cache
                var now = DateTime.UtcNow;
                var recentlyActiveSilos = update.Entries
                    .Where(e => e.Value.Status is SiloStatus.Active or SiloStatus.Joining)
                    .Where(e => !e.Value.HasMissedIAmAlives(clusterMembershipOptions.Value, now));
                foreach (var membershipEntry in recentlyActiveSilos)
                {
                    if (!_metadata.ContainsKey(membershipEntry.Key))
                    {
                        if (_negativeCache.TryGetValue(membershipEntry.Key, out var expiration) && expiration > now)
                        {
                            continue;
                        }
                        try
                        {
                            var metadata = await siloMetadataClient.GetSiloMetadata(membershipEntry.Key).WaitAsync(ct);
                            _metadata.TryAdd(membershipEntry.Key, metadata);
                            _negativeCache.Remove(membershipEntry.Key, out _);
                        }
                        catch(Exception exception)
                        {
                            _negativeCache.TryAdd(membershipEntry.Key, now + negativeCachePeriod);
                            LogErrorFetchingSiloMetadata(logger, exception, membershipEntry.Key);
                        }
                    }
                }

                // Remove entries for members that are now dead
                var deadSilos = update.Entries
                    .Where(e => e.Value.Status == SiloStatus.Dead);
                foreach (var membershipEntry in deadSilos)
                {
                    _metadata.TryRemove(membershipEntry.Key, out _);
                    _negativeCache.Remove(membershipEntry.Key, out _);
                }

                // Remove entries for members that are no longer in the table
                foreach (var silo in _metadata.Keys.ToList())
                {
                    if (!update.Entries.ContainsKey(silo))
                    {
                        _metadata.TryRemove(silo, out _);
                        _negativeCache.Remove(silo, out _);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ignore and continue shutting down.
        }
        catch (Exception exception)
        {
            LogErrorProcessingMembershipUpdates(logger, exception);
        }
        finally
        {
            LogDebugStoppingMembershipProcessor(logger);
        }
    }

    public SiloMetadata GetSiloMetadata(SiloAddress siloAddress) => _metadata.GetValueOrDefault(siloAddress) ?? SiloMetadata.Empty;

    public void SetMetadata(SiloAddress siloAddress, SiloMetadata metadata) => _metadata.TryAdd(siloAddress, metadata);

    public void Dispose() => _cts.Cancel();

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Starting to process membership updates.")]
    private static partial void LogDebugStartProcessingMembershipUpdates(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error fetching metadata for silo {Silo}")]
    private static partial void LogErrorFetchingSiloMetadata(ILogger logger, Exception exception, SiloAddress silo);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error processing membership updates")]
    private static partial void LogErrorProcessingMembershipUpdates(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Stopping membership update processor")]
    private static partial void LogDebugStoppingMembershipProcessor(ILogger logger);
}

