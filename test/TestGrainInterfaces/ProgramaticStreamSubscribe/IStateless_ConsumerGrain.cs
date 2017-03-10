using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Streams;

namespace UnitTests.GrainInterfaces
{
    public interface IStateless_ConsumerGrain: IGrainWithGuidKey
    {
        Task StopConsuming();
        Task<int> GetCountOfOnRemoveFuncCalled();
        Task<int> GetCountOfOnAddFuncCalled();
        Task<int> GetNumberConsumed();
    }

    public interface IImplicitSubscribeGrain: IGrainWithGuidKey
    {
    }
}
