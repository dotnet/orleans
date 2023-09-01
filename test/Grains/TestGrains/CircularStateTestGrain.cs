using TestGrainInterfaces;

namespace TestGrains
{
    public class CircularStateTestGrain : Grain<CircularStateTestState>, ICircularStateTestGrain
    {
        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            var c1 = new CircularTest1();
            var c2 = new CircularTest2();
            c2.CircularTest1List.Add(c1);
            c1.CircularTest2 = c2;

            State.CircularTest1 = c1;
            await WriteStateAsync();
        }
        public Task<CircularTest1> GetState()
        {
            return Task.FromResult(State.CircularTest1);
        }
    }
}
