using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Dashboard.Model;
using Orleans.Dashboard.Model.History;

namespace Orleans.Dashboard.Core;

internal interface IDashboardClient
{
    Task<Immutable<DashboardCounters>> DashboardCounters(string[] exclusions = null);

    Task<Immutable<Dictionary<string, GrainTraceEntry>>> ClusterStats();

    Task<Immutable<ReminderResponse>> GetReminders(int pageNumber, int pageSize);

    Task<Immutable<SiloRuntimeStatistics[]>> HistoricalStats(string siloAddress);

    Task<Immutable<Dictionary<string, string>>> SiloProperties(string siloAddress);

    Task<Immutable<Dictionary<string, string>>> SiloMetadata(string siloAddress);

    Task<Immutable<Dictionary<string, GrainTraceEntry>>> SiloStats(string siloAddress);

    Task<Immutable<StatCounter[]>> GetCounters(string siloAddress);

    Task<Immutable<Dictionary<string, Dictionary<string, GrainTraceEntry>>>> GrainStats(string grainName);

    Task<Immutable<Dictionary<string, GrainMethodAggregate[]>>> TopGrainMethods(int take, string[] exclusions = null);

    Task<Immutable<string>> GetGrainState(string id, string grainType);

    Task<Immutable<string[]>> GetGrainTypes(string[] exclusions = null);
}
