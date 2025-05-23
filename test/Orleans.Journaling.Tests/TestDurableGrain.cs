using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling.Tests;

public class DurableValueTestGrain(
    [FromKeyedServices("name")] IDurableValue<string> name,
    [FromKeyedServices("counter")] IDurableValue<int> counter) : DurableGrain, ITestDurableGrainInterface
{
    private readonly Guid _activationId = Guid.NewGuid();
    private readonly IDurableValue<string> _name = name;
    private readonly IDurableValue<int> _counter = counter;

    public Task SetValues(string name, int counter)
    {
        _name.Value = name;
        _counter.Value = counter;
        return WriteStateAsync().AsTask();
    }

    public Task<(string Name, int Counter)> GetValues() => Task.FromResult((_name.Value!, _counter.Value!));

    public Task<Guid> GetActivationId() => Task.FromResult(_activationId);
}