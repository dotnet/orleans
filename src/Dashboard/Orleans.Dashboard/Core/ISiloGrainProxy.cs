using Orleans.Concurrency;

namespace Orleans.Dashboard.Core;

[Alias("Orleans.Dashboard.Core.ISiloGrainProxy")]
internal interface ISiloGrainProxy : IGrainWithStringKey, ISiloGrainService
{

    [Alias("GetMetadata")]
    Task<Immutable<Dictionary<string, string>>> GetMetadata();
}
