using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class RoundtripSerializationGrain : Grain, IRoundtripSerializationGrain
    {
        public Task<CampaignEnemyTestType> GetEnemyType()
        {
            return Task.FromResult(CampaignEnemyTestType.Enemy2);
        }

        public Task<object> GetClosedGenericValue()
        {
            // use a closed generic that is unlikely to be pre-registered
            var result = new List<ImmutableList<HashSet<Tuple<int, string>>>>();
            return Task.FromResult((object)result);
        }

        // test record support
        public Task<RetVal> GetRetValForParamVal(ParamVal param) => Task.FromResult(new RetVal(param.Value));
    }
}
