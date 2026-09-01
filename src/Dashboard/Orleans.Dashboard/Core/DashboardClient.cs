using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Dashboard.Model;
using Orleans.Dashboard.Model.History;

namespace Orleans.Dashboard.Core;

internal sealed class DashboardClient(IGrainFactory grainFactory) : IDashboardClient
{
    private readonly IDashboardGrain _dashboardGrain = grainFactory.GetGrain<IDashboardGrain>(0);
    private readonly IDashboardRemindersGrain _remindersGrain = grainFactory.GetGrain<IDashboardRemindersGrain>(0);
    private readonly IGrainFactory _grainFactory = grainFactory;

    public async Task<Immutable<DashboardCounters>> DashboardCounters(
        string[]? exclusions,
        CancellationToken cancellationToken = default) =>
        await _dashboardGrain.GetCounters(exclusions, cancellationToken);

    public async Task<Immutable<Dictionary<string, GrainTraceEntry>>> ClusterStats(
        CancellationToken cancellationToken = default) =>
        await _dashboardGrain.GetClusterTracing(cancellationToken);

    public async Task<Immutable<ReminderResponse>> GetReminders(
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        await _remindersGrain.GetReminders(pageNumber, pageSize, cancellationToken);

    public async Task<Immutable<SiloRuntimeStatistics?[]>> HistoricalStats(
        string siloAddress,
        CancellationToken cancellationToken = default) =>
        await Silo(siloAddress).GetRuntimeStatistics(cancellationToken);

    public async Task<Immutable<Dictionary<string, string?>>> SiloProperties(
        string siloAddress,
        CancellationToken cancellationToken = default) =>
        await Silo(siloAddress).GetExtendedProperties(cancellationToken);

    public async Task<Immutable<Dictionary<string, string>>> SiloMetadata(
        string siloAddress,
        CancellationToken cancellationToken = default) =>
        await Silo(siloAddress).GetMetadata(cancellationToken);

    public async Task<Immutable<Dictionary<string, GrainTraceEntry>>> SiloStats(
        string siloAddress,
        CancellationToken cancellationToken = default) =>
        await _dashboardGrain.GetSiloTracing(siloAddress, cancellationToken);

    public async Task<Immutable<StatCounter[]>> GetCounters(
        string siloAddress,
        CancellationToken cancellationToken = default) =>
        await Silo(siloAddress).GetCounters(cancellationToken);

    public async Task<Immutable<Dictionary<string, Dictionary<string, GrainTraceEntry>>>> GrainStats(
        string grainName,
        CancellationToken cancellationToken = default) =>
        await _dashboardGrain.GetGrainTracing(grainName, cancellationToken);

    public async Task<Immutable<Dictionary<string, GrainMethodAggregate[]>>> TopGrainMethods(
        int take,
        string[]? exclusions,
        CancellationToken cancellationToken = default) =>
        await _dashboardGrain.TopGrainMethods(take, exclusions, cancellationToken);

    private ISiloGrainProxy Silo(string siloAddress) => _grainFactory.GetGrain<ISiloGrainProxy>(siloAddress);

    public async Task<Immutable<string>> GetGrainState(
        string? id,
        string? grainType,
        CancellationToken cancellationToken = default) =>
        await _dashboardGrain.GetGrainState(id, grainType, cancellationToken);

    public async Task<Immutable<string[]>> GetGrainTypes(
        string[]? exclusions = null,
        CancellationToken cancellationToken = default) =>
        await _dashboardGrain.GetGrainTypes(exclusions, cancellationToken);

    public async Task<Immutable<LifecycleStageInfo[]>> GetLifecycleStages(CancellationToken cancellationToken = default)
    {
        // All silos run an identical lifecycle, so we only need to ask one of them.
        // Use the management grain to find an active host, then call its dashboard
        // silo grain proxy.
        var management = _grainFactory.GetGrain<IManagementGrain>(0);
        var hosts = await management.GetHosts(onlyActive: true, cancellationToken);
        var siloAddress = hosts
            .Where(x => x.Value == SiloStatus.Active)
            .Select(x => x.Key)
            .FirstOrDefault();
        if (siloAddress is null)
        {
            return new LifecycleStageInfo[0].AsImmutable();
        }

        return await Silo(siloAddress.ToParsableString()).GetLifecycleStages(cancellationToken);
    }
}
