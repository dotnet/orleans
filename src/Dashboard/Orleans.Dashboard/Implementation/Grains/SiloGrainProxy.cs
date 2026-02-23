using System.Collections.Generic;
using System.Collections.Immutable;
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

    public SiloGrainProxy(ISiloGrainClient siloGrainClient, ISiloMetadataCache siloMetadataCache = null)
    {
        var siloAddress = SiloAddress.FromParsableString(this.GetPrimaryKeyString());
        _siloGrainService = siloGrainClient.GrainService(siloAddress);
        _siloMetadata = new Dictionary<string, string>(siloMetadataCache?.GetSiloMetadata(siloAddress).Metadata ?? ImmutableDictionary<string, string>.Empty);
    }

    public Task SetVersion(string orleans, string host) => _siloGrainService.SetVersion(orleans, host);

    public Task ReportCounters(Immutable<StatCounter[]> stats) => _siloGrainService.ReportCounters(stats);

    public Task Enable(bool enabled) => _siloGrainService.Enable(enabled);

    public Task<Immutable<Dictionary<string, string>>> GetExtendedProperties() => _siloGrainService.GetExtendedProperties();

    public Task<Immutable<Dictionary<string, string>>> GetMetadata() => Task.FromResult(_siloMetadata.AsImmutable());

    public Task<Immutable<SiloRuntimeStatistics[]>> GetRuntimeStatistics() => _siloGrainService.GetRuntimeStatistics();

    public Task<Immutable<StatCounter[]>> GetCounters() => _siloGrainService.GetCounters();
}
