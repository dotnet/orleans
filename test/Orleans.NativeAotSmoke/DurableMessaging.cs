using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.DurableMessaging;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Serialization;

using var services = new ServiceCollection()
    .AddSerializerContext(new MessagingSerializerContext())
    .AddDurableMessageType<int>("aot.number.v1")
    .AddDurableMessageType<string>("aot.text.v1")
    .BuildServiceProvider();
var integers = services.GetRequiredKeyedService<DurableMessageType<int>>("aot.number.v1");
var strings = services.GetRequiredKeyedService<DurableMessageType<string>>("aot.text.v1");
var sender = GrainId.Create("aot", "sender");
var receiver = GrainId.Create("aot", "receiver");
var key = HierarchicalKey.Create("aot", "command", "1");
using var outbox = new SmokeOutbox(receiver);
var state = new SmokeState(outbox, strings, sender);
var dispatcher = new DurableInboxDispatcher()
    .Register(integers, state, static (value, target, context) =>
    {
        target.Outbox.SendReply(target.ReplyType, context, target.ReplyDestination, value.ToString());
        target.Value = value;
        context.Complete();
    })
    .Register(strings, new Offset(2), static (value, argument, context) =>
    {
        if (value.Length + argument.Value != 5) throw new InvalidOperationException("Struct handler state was lost.");
        context.Complete();
    });

using var input = integers.Create(key, sender, receiver, 42);
var context = new SmokeContext(input);
await dispatcher.HandleAsync(context, CancellationToken.None);
if (!context.Completed || state.Value != 42 || outbox.Count != 1)
{
    throw new InvalidOperationException("Typed stateful handler did not complete its effect and reply.");
}

var reply = outbox.Messages.Single();
if (reply.MessageId != key.CreateChildKey("result") || reply.SenderId != receiver
    || reply.ReceiverId != sender || strings.Decode(reply) != "42")
{
    throw new InvalidOperationException("Typed reply identity, destination, or body was lost.");
}

using var text = strings.Create(key.CreateChildKey("text"), sender, receiver, "abc");
var textContext = new SmokeContext(text);
await dispatcher.HandleAsync(textContext, CancellationToken.None);
if (!textContext.Completed) throw new InvalidOperationException("Typed struct-argument handler did not complete.");
outbox.Send(integers, key.CreateChildKey("send"), sender, 7);
if (integers.Decode(outbox.Messages.Last()) != 7) throw new InvalidOperationException("Typed send failed.");
Console.WriteLine("NativeAOT typed sends, deterministic replies, and static class/struct handler arguments passed.");

internal readonly record struct Offset(int Value);

[GenerateSerializerContext<int>]
[GenerateSerializerContext<string>]
internal partial class MessagingSerializerContext : SerializerContext;

internal sealed class SmokeState(SmokeOutbox outbox, DurableMessageType<string> replyType, GrainId replyDestination)
{
    public SmokeOutbox Outbox { get; } = outbox;
    public DurableMessageType<string> ReplyType { get; } = replyType;
    public GrainId ReplyDestination { get; } = replyDestination;
    public int Value { get; set; }
}

internal sealed class SmokeContext(DurableEnvelope envelope) : IInboxHandlerContext
{
    public DurableEnvelope Envelope { get; } = envelope;
    public bool Completed { get; private set; }
    public void Complete() => Completed = true;
}

internal sealed class SmokeOutbox(GrainId senderId) : IDurableOutbox, IDisposable
{
    private readonly List<DurableEnvelope> _messages = [];
    public GrainId SenderId { get; } = senderId;
    public int Count => _messages.Count;
    public IEnumerable<DurableEnvelope> Messages => _messages;
    public void Send(DurableEnvelope envelope) => _messages.Add(envelope.Retain());
    public bool TryGetMessage(HierarchicalKey messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope)
    {
        foreach (var message in _messages)
        {
            if (message.MessageId != messageId) continue;
            envelope = message;
            return true;
        }
        envelope = default;
        return false;
    }

    public void Dispose()
    {
        foreach (var envelope in _messages) envelope.Dispose();
    }
}
