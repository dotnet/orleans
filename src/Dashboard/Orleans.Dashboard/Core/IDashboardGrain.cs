using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Dashboard.Model;
using Orleans.Dashboard.Model.History;

namespace Orleans.Dashboard.Core;

internal interface IDashboardGrain : IGrainWithIntegerKey
{
    [OneWay]
    Task InitializeAsync();

    [OneWay]
    Task SubmitTracing(string siloAddress, Immutable<SiloGrainTraceEntry[]> grainCallTime);

    Task<Immutable<DashboardCounters>> GetCounters();

    Task<Immutable<Dictionary<string, Dictionary<string, GrainTraceEntry>>>> GetGrainTracing(string grain);

    Task<Immutable<Dictionary<string, GrainTraceEntry>>> GetClusterTracing();

    Task<Immutable<Dictionary<string, GrainTraceEntry>>> GetSiloTracing(string address);

    Task<Immutable<Dictionary<string, GrainMethodAggregate[]>>> TopGrainMethods(int take);

    Task<Immutable<string>> GetGrainState(string id, string grainType);

    Task<Immutable<string[]>> GetGrainTypes();
}
