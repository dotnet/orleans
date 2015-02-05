using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime.Configuration;

using UnitTestGrainInterfaces;

namespace UnitTestGrains
{
    public class EnumResultGrain : Grain, IEnumResultGrain
    {
        public Task<CampaignEnemyTestType> GetEnemyType()
        {
            return Task.FromResult(CampaignEnemyTestType.Enemy2);
        }

        public Task<ClusterConfiguration> GetConfiguration()
        {
            throw new NotImplementedException();
        }
    }
}
