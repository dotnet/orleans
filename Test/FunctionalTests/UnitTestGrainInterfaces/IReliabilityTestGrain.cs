using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;


namespace UnitTestGrainInterfaces
{
    public interface IReliabilityTestGrain : IGrain
    {
        Task<IReliabilityTestGrain> GetOther();

        Task<string> GetLabel();

        Task SetLabels(string label, int delay);

        Task SetLabel(string label);
    }
}
