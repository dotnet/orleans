using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Services;
using Orleans.Dashboard.Model;

namespace Orleans.Dashboard.Core;

internal interface ISiloGrainService : IGrainService
{
    Task SetVersion(string orleans, string host);

    [OneWay]
    Task ReportCounters(Immutable<StatCounter[]> stats);

    Task Enable(bool enabled);

    Task<Immutable<Dictionary<string, string>>> GetExtendedProperties();

    Task<Immutable<SiloRuntimeStatistics[]>> GetRuntimeStatistics();

    Task<Immutable<StatCounter[]>> GetCounters();
}
