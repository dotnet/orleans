using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IHeterogeneousGrain : IGrainWithIntegerKey
    {
        Task Ping();
    }
}
