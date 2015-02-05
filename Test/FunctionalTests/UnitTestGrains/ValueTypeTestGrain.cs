using System;
using System.Threading.Tasks;
using Orleans;
using UnitTestGrainInterfaces;

namespace UnitTestGrains
{
    public interface IValueTypeTestGrainState : IGrainState
    {
        ValueTypeTestData StateData { get; set; }
    }

    [Serializable]
    public class ValueTypeTestGrain : Grain<IValueTypeTestGrainState>, IValueTypeTestGrain
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
            return Task<CampaignEnemyTestType>.FromResult(CampaignEnemyTestType.Enemy2);
        }

        public Task<ValueTypeTestData> GetStateData()
        {
            return Task<ValueTypeTestData>.FromResult(State.StateData);
        }


        public Task SetStateData(ValueTypeTestData d)
        {
            State.StateData = d;
            return TaskDone.Done;
        }
    }
}
