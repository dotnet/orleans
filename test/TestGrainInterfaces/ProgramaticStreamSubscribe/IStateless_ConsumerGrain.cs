using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IStateless_ConsumerGrain: ISampleStreaming_ConsumerGrain
    {
    }

    public interface IImplicitSubscribeGrain: IGrainWithGuidKey
    {
    }
}
