using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;

namespace UnitTestGrains
{
    public class AsyncGrain : Grain, IAsyncGrain
    {
        int a = 0;

        public Task SetA(int a)
        {
            this.a = a;
            return TaskDone.Done;
        }

        public Task IncrementA()
        {
            a = a+1;
            return TaskDone.Done;
        }

        public Task<int> GetAError(int a)
        {
            throw new Exception("GetAError(a)-Exception");
        }
    }
}
