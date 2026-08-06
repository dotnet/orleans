using Abstractions;
using Orleans;
using Orleans.Configuration;

namespace Grains
{
    [CollectionAgeLimit(Minutes = 2)]
    public class HelloGrain : Grain, IHelloGrain
    {
        public Task<string> SayHello()
        {
            return Task.FromResult($"Hello from Grain {this.GetPrimaryKeyLong()}");
        }
    }
}