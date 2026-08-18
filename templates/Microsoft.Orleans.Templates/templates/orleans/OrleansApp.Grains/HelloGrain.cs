using Orleans.Runtime;
using OrleansApp.Contracts;

namespace OrleansApp.Grains;

public sealed class HelloGrain(
    [PersistentState("hello", "Default")] IPersistentState<int> callCount)
    : Grain, IHelloGrain
{
    public async Task<string> SayHello(string name)
    {
        callCount.State++;
        await callCount.WriteStateAsync();
        return $"Hello, {name}! Call count: {callCount.State}.";
    }
}
