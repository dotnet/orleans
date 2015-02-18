using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using Orleans;

namespace Orleans.Samples.Testing.UnitTests.GrainInterfaces
{
    /// <summary>
    /// Orleans grain communication interface IGrain1
    /// </summary>
    public interface ITestGrain : IGrain
    {
        Task<string> Test(string s);
        Task SetValue(int x);
        Task<int> GetValue();
    }
}
