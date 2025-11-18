using System;
using Orleans.Dashboard.Core;
using Orleans.Runtime;
using Orleans.Runtime.Services;

namespace Orleans.Dashboard.Implementation;

internal sealed class SiloGrainClient(IServiceProvider serviceProvider) : GrainServiceClient<ISiloGrainService>(serviceProvider), ISiloGrainClient
{
    public ISiloGrainService GrainService(SiloAddress destination)
        => GetGrainService(destination);
}
