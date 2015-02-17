using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;


namespace LoadTestGrainInterfaces
{
    public interface IPersistenceLoadTestGrain : IGrain
    {
        Task<int> GetStateValue();
        Task DoStateWrite(int val);
        Task<int> DoStateRead();
        Task Clear();
    }
}
