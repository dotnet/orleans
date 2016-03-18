using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class ValueTypeTestGrainState
    {
        public ValueTypeTestData StateData { get; set; }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    public class ValueTypeTestGrain : Grain<ValueTypeTestGrainState>, IValueTypeTestGrain
    {
        public ValueTypeTestGrain()
        {
            State.StateData = new ValueTypeTestData(7);
        }

        public Task SetState(ValueTypeTestData d)
        {
            State.StateData = d;
            return TaskDone.Done;
        }

        public Task<CampaignEnemyTestType> GetEnemyType()
        {
            return Task.FromResult(CampaignEnemyTestType.Enemy2);
        }

        public Task<ValueTypeTestData> GetStateData()
        {
            return Task.FromResult(State.StateData);
        }


        public Task SetStateData(ValueTypeTestData d)
        {
            State.StateData = d;
            return TaskDone.Done;
        }
    }
}
