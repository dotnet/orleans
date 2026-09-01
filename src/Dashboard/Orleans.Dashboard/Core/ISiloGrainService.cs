using System.Collections.Generic;
using System.Threading;
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
    Task SetVersion(string orleans, string host, CancellationToken cancellationToken = default);

    [OneWay]
    [Alias("ReportCounters")]
    Task ReportCounters(Immutable<StatCounter[]> stats, CancellationToken cancellationToken = default);

    [Alias("Enable")]
    Task Enable(bool enabled, CancellationToken cancellationToken = default);

    [Alias("GetExtendedProperties")]
    Task<Immutable<Dictionary<string, string?>>> GetExtendedProperties(CancellationToken cancellationToken = default);

    [Alias("GetRuntimeStatistics")]
    Task<Immutable<SiloRuntimeStatistics?[]>> GetRuntimeStatistics(CancellationToken cancellationToken = default);

    [Alias("GetCounters")]
    Task<Immutable<StatCounter[]>> GetCounters(CancellationToken cancellationToken = default);

    [Alias("GetLifecycleStages")]
    Task<Immutable<LifecycleStageInfo[]>> GetLifecycleStages(CancellationToken cancellationToken = default);
}
