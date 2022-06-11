using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IDeadlockNonReentrantGrain : IGrainWithIntegerKey
    {
        Task CallNext_1(List<(long GrainId, bool Blocking)> callChain, int currCallIndex);
        Task CallNext_2(List<(long GrainId, bool Blocking)> callChain, int currCallIndex);
    }

    public interface IDeadlockReentrantGrain : IGrainWithIntegerKey
    {
        Task CallNext_1(List<(long GrainId, bool Blocking)> callChain, int currCallIndex);
        Task CallNext_2(List<(long GrainId, bool Blocking)> callChain, int currCallIndex);
    }
}
