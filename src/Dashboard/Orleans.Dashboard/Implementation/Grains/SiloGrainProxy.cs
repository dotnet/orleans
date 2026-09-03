using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Dashboard.Core;
using Orleans.Dashboard.Model;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService.SiloMetadata;

namespace Orleans.Dashboard.Implementation.Grains;

[PreferLocalPlacement]
internal sealed class SiloGrainProxy : Grain, ISiloGrainProxy
{
    private readonly ISiloGrainService _siloGrainService;
    private readonly Dictionary<string, string> _siloMetadata;

    public SiloGrainProxy(ISiloGrainClient siloGrainClient, ISiloMetadataCache? siloMetadataCache = null)
    {
        var siloAddress = SiloAddress.FromParsableString(this.GetPrimaryKeyString());
        _siloGrainService = siloGrainClient.GrainService(siloAddress);
        _siloMetadata = new Dictionary<string, string>(siloMetadataCache?.GetSiloMetadata(siloAddress).Metadata ?? ImmutableDictionary<string, string>.Empty);
    }

    public Task SetVersion(
        string orleans,
        string host,
        CancellationToken cancellationToken = default) =>
        _siloGrainService.SetVersion(orleans, host, cancellationToken);

    public Task ReportCounters(
        Immutable<StatCounter[]> stats,
        CancellationToken cancellationToken = default) =>
        _siloGrainService.ReportCounters(stats, cancellationToken);

    public Task Enable(bool enabled, CancellationToken cancellationToken = default) =>
        _siloGrainService.Enable(enabled, cancellationToken);

    public Task<Immutable<Dictionary<string, string?>>> GetExtendedProperties(
        CancellationToken cancellationToken = default) =>
        _siloGrainService.GetExtendedProperties(cancellationToken);

    public Task<Immutable<Dictionary<string, string>>> GetMetadata(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_siloMetadata.AsImmutable());
    }

    public Task<Immutable<SiloRuntimeStatistics?[]>> GetRuntimeStatistics(
        CancellationToken cancellationToken = default) =>
        _siloGrainService.GetRuntimeStatistics(cancellationToken);

    public Task<Immutable<StatCounter[]>> GetCounters(CancellationToken cancellationToken = default) =>
        _siloGrainService.GetCounters(cancellationToken);

    public Task<Immutable<LifecycleStageInfo[]>> GetLifecycleStages(CancellationToken cancellationToken = default) =>
        _siloGrainService.GetLifecycleStages(cancellationToken);
}
