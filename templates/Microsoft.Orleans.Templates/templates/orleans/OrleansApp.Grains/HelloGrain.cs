using OrleansApp.Contracts;

namespace OrleansApp.Grains;

public sealed class HelloGrain : Grain, IHelloGrain
{
    public Task<string> SayHello(string name) => Task.FromResult($"Hello, {name}!");
}
