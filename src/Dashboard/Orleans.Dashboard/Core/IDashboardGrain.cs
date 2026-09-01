using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Dashboard.Model;
using Orleans.Dashboard.Model.History;

namespace Orleans.Dashboard.Core;

[Alias("Orleans.Dashboard.Core.IDashboardGrain")]
internal interface IDashboardGrain : IGrainWithIntegerKey
{
    [OneWay]
    [Alias("InitializeAsync")]
    Task InitializeAsync(CancellationToken cancellationToken = default);

    [OneWay]
    [Alias("SubmitTracing")]
    Task SubmitTracing(
        string siloAddress,
        Immutable<SiloGrainTraceEntry[]> grainCallTime,
        CancellationToken cancellationToken = default);

    [Alias("GetCounters")]
    Task<Immutable<DashboardCounters>> GetCounters(
        string[]? exclusions = null,
        CancellationToken cancellationToken = default);

    [Alias("GetGrainTracing")]
    Task<Immutable<Dictionary<string, Dictionary<string, GrainTraceEntry>>>> GetGrainTracing(
        string grain,
        CancellationToken cancellationToken = default);

    [Alias("GetClusterTracing")]
    Task<Immutable<Dictionary<string, GrainTraceEntry>>> GetClusterTracing(CancellationToken cancellationToken = default);

    [Alias("GetSiloTracing")]
    Task<Immutable<Dictionary<string, GrainTraceEntry>>> GetSiloTracing(
        string address,
        CancellationToken cancellationToken = default);

    [Alias("TopGrainMethods")]
    Task<Immutable<Dictionary<string, GrainMethodAggregate[]>>> TopGrainMethods(
        int take,
        string[]? exclusions = null,
        CancellationToken cancellationToken = default);

    [Alias("GetGrainState")]
    Task<Immutable<string>> GetGrainState(
        string? id,
        string? grainType,
        CancellationToken cancellationToken = default);

    [Alias("GetGrainTypes")]
    Task<Immutable<string[]>> GetGrainTypes(
        string[]? exclusions = null,
        CancellationToken cancellationToken = default);
}
