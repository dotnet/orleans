using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;

namespace UnitTests.GrainInterfaces;

public interface IStatelessWorkerScalingGrain : IGrainWithIntegerKey
{
    [AlwaysInterleave]
    Task Wait();

    [AlwaysInterleave]
    Task Release();

    [AlwaysInterleave]
    Task<int> GetActivation();
}
