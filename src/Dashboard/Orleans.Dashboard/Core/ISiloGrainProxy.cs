using Orleans.Concurrency;
using System.Threading;

namespace Orleans.Dashboard.Core;

[Alias("Orleans.Dashboard.Core.ISiloGrainProxy")]
internal interface ISiloGrainProxy : IGrainWithStringKey, ISiloGrainService
{

    [Alias("GetMetadata")]
    Task<Immutable<Dictionary<string, string>>> GetMetadata(CancellationToken cancellationToken = default);
}
