using Orleans.Runtime;

namespace HelloWorld;

public sealed class HelloGrain : IGrainBase, IHelloGrain
{
    public IGrainContext GrainContext { get; }

    public HelloGrain(IGrainContext context) =>
        GrainContext = context;

    public ValueTask<string> SayHello(string greeting)
    {
        return ValueTask.FromResult($"Hello, {greeting}!");
    }
}
