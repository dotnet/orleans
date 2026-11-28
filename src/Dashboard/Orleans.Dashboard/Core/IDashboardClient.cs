using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Dashboard.Model;
using Orleans.Dashboard.Model.History;
using Orleans.Runtime;

namespace Orleans.Dashboard.Core;

internal interface IDashboardClient
{
    Task<Immutable<DashboardCounters>> DashboardCounters(
        string[]? exclusions = null,
        CancellationToken cancellationToken = default);

    Task<Immutable<Dictionary<string, GrainTraceEntry>>> ClusterStats(CancellationToken cancellationToken = default);

    Task<Immutable<ReminderResponse>> GetReminders(
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Immutable<AdvancedReminderResponse>> GetAdvancedReminders(
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Immutable<SiloRuntimeStatistics?[]>> HistoricalStats(
        string siloAddress,
        CancellationToken cancellationToken = default);

    Task<Immutable<Dictionary<string, string?>>> SiloProperties(
        string siloAddress,
        CancellationToken cancellationToken = default);

    Task<Immutable<Dictionary<string, string>>> SiloMetadata(
        string siloAddress,
        CancellationToken cancellationToken = default);

    Task<Immutable<Dictionary<string, GrainTraceEntry>>> SiloStats(
        string siloAddress,
        CancellationToken cancellationToken = default);

    Task<Immutable<StatCounter[]>> GetCounters(
        string siloAddress,
        CancellationToken cancellationToken = default);

    Task<Immutable<Dictionary<string, Dictionary<string, GrainTraceEntry>>>> GrainStats(
        string grainName,
        CancellationToken cancellationToken = default);

    Task<Immutable<Dictionary<string, GrainMethodAggregate[]>>> TopGrainMethods(
        int take,
        string[]? exclusions = null,
        CancellationToken cancellationToken = default);

    Task<Immutable<string>> GetGrainState(
        string? id,
        string? grainType,
        CancellationToken cancellationToken = default);

    Task<Immutable<string[]>> GetGrainTypes(
        string[]? exclusions = null,
        CancellationToken cancellationToken = default);

    Task<Immutable<LifecycleStageInfo[]>> GetLifecycleStages(CancellationToken cancellationToken = default);
}
