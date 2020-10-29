using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    class CallChainGrain1 : Grain, ICallChainGrain1
    {
        public async Task<int> Run(int v)
        {
            if (v == 0)
                return 0;

            var res = await GrainFactory.GetGrain<ICallChainGrain2>(this.GetPrimaryKeyLong()).Run(v - 1);
            return res + 1;
        }
    }

    class CallChainGrain2 : Grain, ICallChainGrain2
    {
        public async Task<int> Run(int v)
        {
            if (v == 0)
                return 0;

            var res = await GrainFactory.GetGrain<ICallChainGrain3>(this.GetPrimaryKeyLong()).Run(v - 1);
            return res + 1;
        }
    }

    class CallChainGrain3 : Grain, ICallChainGrain3
    {
        public async Task<int> Run(int v)
        {
            if (v == 0)
                return 0;

            var res = await GrainFactory.GetGrain<ICallChainGrain1>(this.GetPrimaryKeyLong()).Run(v - 1);
            return res + 1;
        }
    }
}
