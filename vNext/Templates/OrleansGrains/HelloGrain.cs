using System;
using System.Threading.Tasks;
using Orleans;
using OrleansInterface;

namespace OrleansGrains
{
    public class HelloGrain : Grain, IHelloGrain
    {
        public Task<string> SayHello(string greeting) => Task.FromResult($"You said: '{greeting}', I say: Hello!");
    }
}
