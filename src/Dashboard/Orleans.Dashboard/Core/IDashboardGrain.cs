using System.Collections.Generic;
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
    Task InitializeAsync();

    [OneWay]
    [Alias("SubmitTracing")]
    Task SubmitTracing(string siloAddress, Immutable<SiloGrainTraceEntry[]> grainCallTime);

    [Alias("GetCounters")]
    Task<Immutable<DashboardCounters>> GetCounters(string[] exclusions = null);

    [Alias("GetGrainTracing")]
    Task<Immutable<Dictionary<string, Dictionary<string, GrainTraceEntry>>>> GetGrainTracing(string grain);

    [Alias("GetClusterTracing")]
    Task<Immutable<Dictionary<string, GrainTraceEntry>>> GetClusterTracing();

    [Alias("GetSiloTracing")]
    Task<Immutable<Dictionary<string, GrainTraceEntry>>> GetSiloTracing(string address);

    [Alias("TopGrainMethods")]
    Task<Immutable<Dictionary<string, GrainMethodAggregate[]>>> TopGrainMethods(int take, string[] exclusions = null);

    [Alias("GetGrainState")]
    Task<Immutable<string>> GetGrainState(string id, string grainType);

    [Alias("GetGrainTypes")]
    Task<Immutable<string[]>> GetGrainTypes(string[] exclusions = null);
}
