using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTestGrains
{
    public interface IConcurrentGrain : IGrain
    {
        Task Initialize(int index);

        //[ReadOnly]
        Task<int> A();
        //[ReadOnly]
        Task<int> B(int time);

        Task<List<int>> ModifyReturnList_Test();

        Task Initialize_2(int index);
        Task<int> TailCall_Caller(IConcurrentReentrantGrain another, bool doCW);
        Task<int> TailCall_Resolver(IConcurrentReentrantGrain another);
    }

    public interface IConcurrentReentrantGrain : IGrain
    {
        Task Initialize_2(int index);
        Task<int> TailCall_Called();
        Task<int> TailCall_Resolve();
    }
}
