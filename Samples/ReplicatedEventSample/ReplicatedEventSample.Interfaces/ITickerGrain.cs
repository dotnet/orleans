using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace ReplicatedEventSample.Interfaces
{
    public interface ITickerGrain : IGrainWithIntegerKey
    {

        Task SomethingHappened(string what);

        Task<string> GetTickerLine();
    }
}
