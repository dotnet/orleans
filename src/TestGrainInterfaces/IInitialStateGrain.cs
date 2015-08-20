using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IInitialStateGrain : IGrainWithIntegerKey
    {
        Task<List<string>> GetNames();
    }
}
