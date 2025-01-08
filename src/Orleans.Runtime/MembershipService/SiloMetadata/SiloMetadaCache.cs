using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Internal;

#nullable enable
namespace Orleans.Runtime.MembershipService.SiloMetadata;

internal class SiloMetadataCache(
    ISiloMetadataClient siloMetadataClient,
    MembershipTableManager membershipTableManager,
    ILogger<SiloMetadataCache> logger)
    : ISiloMetadataCache, ILifecycleParticipant<ISiloLifecycle>, IDisposable
{
    private readonly ConcurrentDictionary<SiloAddress, SiloMetadata> _metadata = new();
    private readonly CancellationTokenSource _cts = new();

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
        var tasks = new List<Task>(1);
        var cancellation = new CancellationTokenSource();
        Task OnRuntimeInitializeStart(CancellationToken _)
        {
            tasks.Add(Task.Run(() => this.ProcessMembershipUpdates(cancellation.Token)));
            return Task.CompletedTask;
        }

        async Task OnRuntimeInitializeStop(CancellationToken ct)
        {
            cancellation.Cancel(throwOnFirstException: false);
            var shutdownGracePeriod = Task.WhenAll(Task.Delay(ClusterMembershipOptions.ClusteringShutdownGracePeriod), ct.WhenCancelled());
            await Task.WhenAny(shutdownGracePeriod, Task.WhenAll(tasks));
        }

        lifecycle.Subscribe(
            nameof(ClusterMembershipService),
            ServiceLifecycleStage.RuntimeInitialize,
            OnRuntimeInitializeStart,
            OnRuntimeInitializeStop);
    }


    private async Task ProcessMembershipUpdates(CancellationToken ct)
    {
        try
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Starting to process membership updates");
            await foreach (var update in membershipTableManager.MembershipTableUpdates.WithCancellation(ct))
            {
                // Add entries for members that aren't already in the cache
                foreach (var membershipEntry in update.Entries.Where(e => e.Value.Status != SiloStatus.Dead))
                {
                    if (!_metadata.ContainsKey(membershipEntry.Key))
                    {
                        try
                        {
                            var metadata = await siloMetadataClient.GetSiloMetadata(membershipEntry.Key);
                            _metadata.TryAdd(membershipEntry.Key, metadata);
                        }
                        catch(Exception exception)
                        {
                            logger.LogError(exception, "Error fetching metadata for silo {Silo}", membershipEntry.Key);
                        }
                    }
                }

                // Remove entries for members that are now dead
                foreach (var membershipEntry in update.Entries.Where(e => e.Value.Status == SiloStatus.Dead))
                {
                    _metadata.TryRemove(membershipEntry.Key, out _);
                }

                // Remove entries for members that are no longer in the table
                foreach (var silo in _metadata.Keys.ToList())
                {
                    if (!update.Entries.ContainsKey(silo))
                    {
                        _metadata.TryRemove(silo, out _);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Error processing membership updates");
        }
        finally
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Stopping membership update processor");
        }
    }

    public SiloMetadata GetMetadata(SiloAddress siloAddress) => _metadata.GetValueOrDefault(siloAddress) ?? SiloMetadata.Empty;

    public void SetMetadata(SiloAddress siloAddress, SiloMetadata metadata) => _metadata.TryAdd(siloAddress, metadata);

    public void Dispose() => _cts.Cancel();
}