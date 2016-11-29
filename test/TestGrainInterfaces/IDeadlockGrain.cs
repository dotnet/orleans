using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IDeadlockNonReentrantGrain : IGrainWithIntegerKey
    {
        Task CallNext_1(List<Tuple<long, bool>> callChain, int currCallIndex);
        Task CallNext_2(List<Tuple<long, bool>> callChain, int currCallIndex);
    }

    public interface IDeadlockReentrantGrain : IGrainWithIntegerKey
    {
        Task CallNext_1(List<Tuple<long, bool>> callChain, int currCallIndex);
        Task CallNext_2(List<Tuple<long, bool>> callChain, int currCallIndex);
    }
}
