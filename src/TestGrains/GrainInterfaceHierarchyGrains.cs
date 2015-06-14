
using System.Threading.Tasks;
using Orleans;
using TestGrainInterfaces;

namespace TestGrains
{
    public class DoSomethingEmptyGrain : Grain, IDoSomethingEmptyGrain
    {
        public Task<string> DoIt()
        {
            return Task.FromResult(GetType().Name);
        }
    }

    public class DoSomethingEmptyWithMoreGrain : Grain, IDoSomethingEmptyWithMoreGrain
    {
        public Task<string> DoIt()
        {
            return Task.FromResult(GetType().Name);
        }

        public Task<string> DoMore()
        {
            return Task.FromResult(GetType().Name);
        }
    }

    public class DoSomethingWithMoreGrain : Grain, IDoSomethingWithMoreGrain
    {
        public Task<string> DoIt()
        {
            return Task.FromResult(GetType().Name);
        }

        public Task<string> DoThat()
        {
            return Task.FromResult(GetType().Name);
        }

        public Task<string> DoMore()
        {
            return Task.FromResult(GetType().Name);
        }
    }

    public class DoSomethingWithMoreEmptyGrain : Grain, IDoSomethingWithMoreEmptyGrain
    {
        public Task<string> DoIt()
        {
            return Task.FromResult(GetType().Name);
        }

        public Task<string> DoMore()
        {
            return Task.FromResult(GetType().Name);
        }
    }



    public class DoSomethingCombinedGrain : Grain, IDoSomethingCombinedGrain
    {
        public Task<string> DoIt()
        {
            return Task.FromResult(GetType().Name);
        }

        public Task<string> DoMore()
        {
            return Task.FromResult(GetType().Name);
        }

        public Task<string> DoThat()
        {
            return Task.FromResult(GetType().Name);
        }
    }
}
