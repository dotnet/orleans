using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Dashboard.Model;
using Orleans.Dashboard.Core;

namespace Orleans.Dashboard.Implementation.Grains;

[PreferLocalPlacement]
internal sealed class SiloGrainProxy : Grain, ISiloGrainProxy
{
    private readonly ISiloGrainService _siloGrainService;

    public SiloGrainProxy(ISiloGrainClient siloGrainClient)
    {
        _siloGrainService = siloGrainClient.GrainService(
            SiloAddress.FromParsableString(this.GetPrimaryKeyString())
        );
    }

    public Task SetVersion(string orleans, string host) => _siloGrainService.SetVersion(orleans, host);

    public Task ReportCounters(Immutable<StatCounter[]> stats) => _siloGrainService.ReportCounters(stats);

    public Task Enable(bool enabled) => _siloGrainService.Enable(enabled);

    public Task<Immutable<Dictionary<string, string>>> GetExtendedProperties() => _siloGrainService.GetExtendedProperties();

    public Task<Immutable<SiloRuntimeStatistics[]>> GetRuntimeStatistics() => _siloGrainService.GetRuntimeStatistics();

    public Task<Immutable<StatCounter[]>> GetCounters() => _siloGrainService.GetCounters();
}
