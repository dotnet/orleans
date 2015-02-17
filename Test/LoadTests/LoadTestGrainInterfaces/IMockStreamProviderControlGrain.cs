using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace LoadTestGrainInterfaces
{
    public interface IMockStreamProviderControlGrain : IGrain
    {
        Task StartProducing();
        Task<bool> ShouldProviderProduceEvents();
    }
}
