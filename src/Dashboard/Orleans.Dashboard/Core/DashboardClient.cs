using System.Collections.Generic;
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

    public async Task<Immutable<DashboardCounters>> DashboardCounters() => await _dashboardGrain.GetCounters();

    public async Task<Immutable<Dictionary<string, GrainTraceEntry>>> ClusterStats() => await _dashboardGrain.GetClusterTracing();

    public async Task<Immutable<ReminderResponse>> GetReminders(int pageNumber, int pageSize) => await _remindersGrain.GetReminders(pageNumber, pageSize);

    public async Task<Immutable<SiloRuntimeStatistics[]>> HistoricalStats(string siloAddress) => await Silo(siloAddress).GetRuntimeStatistics();

    public async Task<Immutable<Dictionary<string, string>>> SiloProperties(string siloAddress) => await Silo(siloAddress).GetExtendedProperties();

    public async Task<Immutable<Dictionary<string, GrainTraceEntry>>> SiloStats(string siloAddress) => await _dashboardGrain.GetSiloTracing(siloAddress);

    public async Task<Immutable<StatCounter[]>> GetCounters(string siloAddress) => await Silo(siloAddress).GetCounters();

    public async Task<Immutable<Dictionary<string, Dictionary<string, GrainTraceEntry>>>> GrainStats(
        string grainName) => await _dashboardGrain.GetGrainTracing(grainName);

    public async Task<Immutable<Dictionary<string, GrainMethodAggregate[]>>> TopGrainMethods(int take) => await _dashboardGrain.TopGrainMethods(take);

    private ISiloGrainService Silo(string siloAddress) => _grainFactory.GetGrain<ISiloGrainProxy>(siloAddress);

    public async Task<Immutable<string>> GetGrainState(string id, string grainType) => await _dashboardGrain.GetGrainState(id, grainType);

    public async Task<Immutable<string[]>> GetGrainTypes() => await _dashboardGrain.GetGrainTypes();
}
