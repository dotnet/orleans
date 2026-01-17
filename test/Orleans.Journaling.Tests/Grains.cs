using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling.Messaging;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using System.Collections.Generic;

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

/// <summary>
/// Test grain interface for RequestContext propagation in durable messaging.
/// </summary>
public interface IRequestContextTestGrain : IGrainWithGuidKey
{
    Task SendTestMessage(string message);
    Task<Dictionary<string, object>?> GetCapturedRequestContext();
}

/// <summary>
/// Test grain for RequestContext propagation in durable messaging.
/// Implementation that captures RequestContext when messages are received.
/// </summary>
[GrainType("journaling-requestcontexttest")]
public class RequestContextTestGrain(
    [FromKeyedServices("inbox")] IDurableInbox inbox,
    [FromKeyedServices("outbox")] IDurableOutbox outbox) : DurableGrain, IRequestContextTestGrain
{
    private readonly IDurableInbox _inbox = inbox;
    private readonly IDurableOutbox _outbox = outbox;
    private Dictionary<string, object>? _capturedContext;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Register handler that captures RequestContext
        _inbox.RegisterHandler("test-message", new TestMessageHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task SendTestMessage(string message)
    {
        // Send message to self to trigger handler
        var inboxExtension = this.AsReference<IDurableInboxExtension>();
        var sessionPool = ServiceProvider.GetRequiredService<SerializerSessionPool>();
        
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = this.GetGrainId()
        };
        
        var envelope = builder
            .To(this.GetGrainId(), "test-message")
            .WithBody(message)
            .Build();

        await inboxExtension.DeliverAsync(envelope, new DeliveryOptions { PollTimeout = TimeSpan.Zero }, CancellationToken.None);
    }

    public Task<Dictionary<string, object>?> GetCapturedRequestContext()
    {
        return Task.FromResult(_capturedContext);
    }

    private class TestMessageHandler(RequestContextTestGrain grain) : IInboxHandler
    {
        public bool CanHandle(IInboxHandlerContext context) => true;

        public ValueTask HandleAsync(DurableEnvelope envelope, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            // Capture the current RequestContext (which should be restored from the envelope)
            var entries = RequestContext.Entries;
            grain._capturedContext = entries.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            return ValueTask.CompletedTask;
        }
    }
}

