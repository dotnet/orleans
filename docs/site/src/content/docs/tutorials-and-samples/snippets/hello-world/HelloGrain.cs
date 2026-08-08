using Orleans;

namespace HelloWorld;

public sealed class HelloGrain : Grain, IHello
{
    public ValueTask<string> SayHello(string greeting) =>
        ValueTask.FromResult($"Hello, {greeting}!");
}
