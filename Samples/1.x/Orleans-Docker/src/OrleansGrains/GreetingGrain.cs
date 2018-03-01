using System;
using System.Threading.Tasks;
using Orleans;
using OrleansGrainInterfaces;

namespace OrleansGrains
{
    public class GreetingGrain : Grain, IGreetingGrain
    {
        public Task<string> SayHello(string name)
        {
            return Task.FromResult($"Hello from Orleans, {name}");
        }
    }
}
