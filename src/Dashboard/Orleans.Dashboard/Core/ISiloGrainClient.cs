using Orleans.Runtime;
using Orleans.Services;

namespace Orleans.Dashboard.Core;

internal interface ISiloGrainClient : IGrainServiceClient<ISiloGrainService>
{
    ISiloGrainService GrainService(SiloAddress destination);
}
