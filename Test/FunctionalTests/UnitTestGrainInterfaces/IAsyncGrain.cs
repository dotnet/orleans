using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;

namespace UnitTestGrains
{
    /// <summary>
    /// A simple grain that allows to set two agruments and then multiply them.
    /// </summary>
    ///    
    /// 
    public interface IAsyncGrain : IGrain
    {
        Task SetA(int a);
        Task IncrementA();
        Task<int> GetAError(int a);
    }
}
