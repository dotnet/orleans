using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime.Configuration;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
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
