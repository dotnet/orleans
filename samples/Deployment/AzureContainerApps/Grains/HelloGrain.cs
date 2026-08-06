using Abstractions;
using Orleans;

namespace Grains
{
    public class HelloGrain : Grain, IHelloGrain
    {
        public Task<string> SayHello()
        {
            return Task.FromResult($"Hello from Grain {this.GetPrimaryKeyString()}");
        }
    }
}