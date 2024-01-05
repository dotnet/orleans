using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class FirstGrain : Grain, IFirstGrain
    {
        public async Task Start(Guid guid1, Guid guid2)
        {
            var acceptedByUserTask = GrainFactory.GetGrain<ISecondGrain>(guid2).SecondGrainMethod(this.GetPrimaryKey());
            var callOwnerUserTask = GrainFactory.GetGrain<ISecondGrain>(guid1).SecondGrainMethod(this.GetPrimaryKey());
            await Task.WhenAll(acceptedByUserTask, callOwnerUserTask);
        }
    }

    public class SecondGrain : Grain, ISecondGrain
    {
        public async Task SecondGrainMethod(Guid guid)
        {
            var keys = new[] { "AAA", "BBB" };

            var tasks = new List<Task>();
            foreach (var key in keys)
            {
                tasks.Add(GrainFactory.GetGrain<IThirdGrain>(key).ThirdGrainMethod(guid));
            }

            await Task.WhenAll(tasks);
        }
    }

    [GenerateSerializer]
    public class ThirdGrainState
    {
    }

    public class ThirdGrain : Grain<ThirdGrainState>, IThirdGrain
    {
        private int inFlightCounter = 0;

        public async Task ThirdGrainMethod(Guid userId)
        {
            this.inFlightCounter++;

            if (this.inFlightCounter > 1)
                throw new Exception("More than 1 in flight call too this method!");

            await this.WriteStateAsync();
            this.inFlightCounter--;
        }
    }
}
