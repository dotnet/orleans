using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using Orleans;

using Orleans.Samples.Testing.UnitTests.GrainInterfaces;

namespace Orleans.Samples.Testing.UnitTests.Grains
{
    /// <summary>
    /// Orleans grain implementation class Grain1.
    /// </summary>
    public class TestGrain : Orleans.Grain, ITestGrain
    {
        private int _x=0;
        public Task<string> Test(string s)
        {
            return Task.FromResult("ACK:" + s);
        }

        public Task SetValue(int x)
        {
            _x = x;
            return TaskDone.Done;
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(_x);
        }
    }
}
