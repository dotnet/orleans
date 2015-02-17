using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;

namespace UnitTestGrains
{
    public interface ICatalogTestGrain : IGrainWithStringKey
    {
        Task Initialize();
        Task BlastCallNewGrains(int nGrains, long startingKey, int nCallsToEach);
    }
}
