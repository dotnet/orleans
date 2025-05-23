using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling.Tests;

[GenerateSerializer]
public sealed record TestDurableGrainState(string Name, int Counter);

public class TestDurableGrain(
    [FromKeyedServices("state")] IPersistentState<TestDurableGrainState> state) : DurableGrain, ITestDurableGrain
{
    private readonly Guid _activationId = Guid.NewGuid();
    public Task<string> GetName() => Task.FromResult(state.State.Name);
    public Task<int> GetCounter() => Task.FromResult(state.State.Counter);

    public async Task SetTestValues(string name, int counter)
    {
        state.State = new(name, counter);
        await WriteStateAsync();
    }

    public Task<Guid> GetActivationId() => Task.FromResult(_activationId);
}

public class TestDurableGrainWithComplexState(
    [FromKeyedServices("person")] IDurableValue<TestPerson> person,
    [FromKeyedServices("list")] IDurableList<string> list) : DurableGrain, ITestDurableGrainWithComplexState
{
    private readonly Guid _activationId = Guid.NewGuid();
    private readonly IDurableValue<TestPerson> _person = person;
    private readonly IDurableList<string> _list = list;

    public Task<TestPerson> GetPerson() => Task.FromResult(_person.Value ?? new TestPerson());
    public Task<IReadOnlyList<string>> GetItems() => Task.FromResult<IReadOnlyList<string>>(_list.AsReadOnly());

    public async Task SetTestValues(TestPerson person, List<string> items)
    {
        _person.Value = person;
        _list.Clear();
        _list.AddRange(items);
        await WriteStateAsync();
    }

    public Task<Guid> GetActivationId() => Task.FromResult(_activationId);
}

public interface ITestDurableGrain : IGrainWithGuidKey
{
    Task<Guid> GetActivationId();
    Task SetTestValues(string name, int counter);
    Task<string> GetName();
    Task<int> GetCounter();
}

public interface ITestDurableGrainWithComplexState : IGrainWithGuidKey
{
    Task<Guid> GetActivationId();
    Task SetTestValues(TestPerson person, List<string> items);
    Task<TestPerson> GetPerson();
    Task<IReadOnlyList<string>> GetItems();
}

