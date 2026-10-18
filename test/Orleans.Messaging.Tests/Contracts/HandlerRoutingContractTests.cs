using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Messaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class HandlerRoutingContractTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddSerializer().BuildServiceProvider();

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task Dispatch_SameSubjectRegisteredTwice_FirstRegistrationWins()
    {
        var type = CreateType("orders/created");
        var calls = new List<string>();
        var dispatcher = new TestInbox()
            .Register(type, (body, context) => { calls.Add($"first:{body}"); context.Complete(); })
            .Register(type, (body, context) => { calls.Add($"second:{body}"); context.Complete(); });
        var context = CreateContext(type, "a");

        await dispatcher.HandleAsync(context, CancellationToken.None);

        Assert.Equal(["first:a"], calls);
        Assert.Equal(1, context.Completions);
    }

    [Fact]
    public async Task Dispatch_ConsultsCanHandleInRegistrationOrder()
    {
        var order = new List<string>();
        var dispatcher = new TestInbox();
        dispatcher.Register(new ProbeHandler("declines", canHandle: false, order));
        dispatcher.Register(new ProbeHandler("accepts", canHandle: true, order));
        dispatcher.Register(new ProbeHandler("unreached", canHandle: true, order));

        await dispatcher.HandleAsync(CreateContext(CreateType("any"), "body"), CancellationToken.None);

        Assert.Equal(["declines?", "accepts?", "accepts!"], order);
    }

    [Fact]
    public async Task Dispatch_NoMatchingHandler_ThrowsWithSubject()
    {
        var dispatcher = new TestInbox().Register(CreateType("known"), (_, context) => context.Complete());
        var context = CreateContext(CreateType("unknown"), "body");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.HandleAsync(context, CancellationToken.None).AsTask());

        Assert.Equal("No handler is registered for message subject 'unknown'.", exception.Message);
        Assert.Equal(0, context.Completions);
    }

    [Fact]
    public void Dispatcher_IsEmptyUntilHandlerIsRegistered()
    {
        var dispatcher = new TestInbox();
        Assert.True(dispatcher.IsEmpty);

        dispatcher.Register(CreateType("subject"), (_, context) => context.Complete());

        Assert.False(dispatcher.IsEmpty);
    }

    [Fact]
    public void HandlerNotFound_RoundTripsStatusAndDiagnostic()
    {
        var result = DeliveryResult.HandlerNotFound();
        var serializer = _services.GetRequiredService<Serializer<DeliveryResult>>();

        var decoded = serializer.Deserialize(serializer.SerializeToArray(result));

        Assert.Equal(DeliveryStatus.HandlerNotFound, decoded.Status);
        Assert.Equal(result.Message, decoded.Message);
    }

    private MessageType<string> CreateType(string subject) =>
        new(subject, _services.GetRequiredService<Serializer<string>>());

    private static FakeContext CreateContext(MessageType<string> type, string body) =>
        new(new InboxMessage
        {
            Envelope = type.Create(MessageId.Create(Guid.NewGuid().ToString("N")), GrainId.Create("test", "receiver"), body).Envelope
        });

    private sealed class FakeContext(InboxMessage message) : IInboxHandlerContext
    {
        public int Completions { get; private set; }
        public InboxMessage Message { get; } = message;
        public void Complete() => Completions++;
        public void Fail(string reason) => throw new NotSupportedException();
    }

    private sealed class ProbeHandler(string name, bool canHandle, List<string> order) : IInboxHandler
    {
        public bool CanHandle(InboxMessage message)
        {
            order.Add($"{name}?");
            return canHandle;
        }

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            order.Add($"{name}!");
            return ValueTask.CompletedTask;
        }
    }
}
