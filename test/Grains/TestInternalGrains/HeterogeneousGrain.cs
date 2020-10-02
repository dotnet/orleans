using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains.A
{
    public class HeterogeneousGrain : Grain, IHeterogeneousGrain
    {
        public Task Ping() => Task.CompletedTask;
    }
}

namespace UnitTests.Grains.B
{
    public class HeterogeneousGrain : Grain, IHeterogeneousGrain
    {
        public Task Ping() => Task.CompletedTask;
    }
}
