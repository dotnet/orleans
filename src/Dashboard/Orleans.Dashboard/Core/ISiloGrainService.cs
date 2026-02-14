using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Services;
using Orleans.Dashboard.Model;

namespace Orleans.Dashboard.Core;

[Alias("Orleans.Dashboard.Core.ISiloGrainService")]
internal interface ISiloGrainService : IGrainService
{
    [Alias("SetVersion")]
    Task SetVersion(string orleans, string host);

    [OneWay]
    [Alias("ReportCounters")]
    Task ReportCounters(Immutable<StatCounter[]> stats);

    [Alias("Enable")]
    Task Enable(bool enabled);

    [Alias("GetExtendedProperties")]
    Task<Immutable<Dictionary<string, string>>> GetExtendedProperties();

    [Alias("GetRuntimeStatistics")]
    Task<Immutable<SiloRuntimeStatistics[]>> GetRuntimeStatistics();

    [Alias("GetCounters")]
    Task<Immutable<StatCounter[]>> GetCounters();
}
