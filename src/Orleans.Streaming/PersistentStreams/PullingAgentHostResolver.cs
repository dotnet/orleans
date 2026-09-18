using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Versions;

namespace Orleans.Streams;

internal sealed class PullingAgentHostResolver(
    IClusterManifestProvider manifestProvider,
    GrainVersionManifest versionManifest,
    IClusterMembershipService membership,
    IInternalGrainFactory grainFactory,
    ILogger<PullingAgentHostResolver> logger)
{
    internal Task<SiloAddress[]> GetEligibleSilos(
        string providerName,
        QueueId? queueId,
        GrainType grainType,
        GrainInterfaceType interfaceType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var supported = versionManifest.GetSupportedSilos(
            grainType, interfaceType, [versionManifest.GetLocalVersion(interfaceType)]).Result;
        return FilterEligibleSilos(providerName, queueId, grainType, supported.Values.SelectMany(static silos => silos), cancellationToken);
    }

    internal async Task<SiloAddress[]> FilterEligibleSilos(
        string providerName,
        QueueId? queueId,
        GrainType grainType,
        IEnumerable<SiloAddress> compatibleSilos,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manifests = manifestProvider.Current.Silos;
        var members = membership.CurrentSnapshot.Members;
        var candidates = compatibleSilos.Where(silo =>
            members.TryGetValue(silo, out var member) && member.Status == SiloStatus.Active
            && manifests.TryGetValue(silo, out var manifest)
            && manifest.Grains.TryGetValue(grainType, out var properties)
            && properties.Properties.ContainsKey(PullingAgentPlacementDirector.ProviderPropertyPrefix + providerName))
            .Distinct().ToArray();
        var eligibility = await Task.WhenAll(candidates.Select(silo => IsEligible(silo)))
            .WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return candidates.Where((_, index) => eligibility[index]).ToArray();

        async Task<bool> IsEligible(SiloAddress silo)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await grainFactory
                    .GetSystemTarget<IPullingAgentRuntime>(PullingAgentRuntime.TargetType, silo)
                    .IsEligible(providerName, queueId, cancellationToken);
            }
            catch (Exception exception) when (exception is SiloUnavailableException
                or OrleansMessageRejectionException or TimeoutException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Active membership is an observation, not a guarantee that this probe can reach the silo.
                logger.LogWarning(exception,
                    "Failed to check pulling-agent host eligibility for provider {ProviderName}, queue {QueueId}, silo {Silo}.",
                    providerName, queueId, silo);
                return false;
            }
        }
    }
}
