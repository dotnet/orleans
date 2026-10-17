using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Messaging;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Serialization;

using var services = new ServiceCollection()
    .AddSerializerContext(new MessagingSerializerContext())
    .AddMessageType<int>("aot.number.v1")
    .AddMessageType<string>("aot.text.v1")
    .BuildServiceProvider();
var integers = services.GetRequiredKeyedService<MessageType<int>>("aot.number.v1");
var strings = services.GetRequiredKeyedService<MessageType<string>>("aot.text.v1");
var sender = GrainId.Create("aot", "sender");
var receiver = GrainId.Create("aot", "receiver");
var key = MessageId.Create("aot", "command", "1");
var outbox = new SmokeOutbox();
var state = new SmokeState(outbox, strings, sender);
var dispatcher = new InboxDispatcher([]);
new SmokeInbox(dispatcher)
    .Register(integers, state, static (value, target, context) =>
    {
        target.Outbox.SendReply(target.ReplyType, context, target.ReplyDestination, value.ToString());
        target.Value = value;
        context.Complete();
    })
    .Register(strings, new Offset(2), static (value, argument, context) =>
    {
        if (value.Length == 0)
        {
            context.Fail("Text is required.");
            return;
        }
        if (value.Length + argument.Value != 5) throw new InvalidOperationException("Struct handler state was lost.");
        context.Complete();
    });

var input = integers.Create(key, receiver, 42);
var context = new SmokeContext(input.ToInboxMessage());
await dispatcher.HandleAsync(context, CancellationToken.None);
if (!context.Completed || state.Value != 42 || outbox.Count != 1)
{
    throw new InvalidOperationException("Typed stateful handler did not complete its effect and reply.");
}

var reply = outbox.Messages.Single();
if (reply.Envelope.MessageId != new MessageId(key.Key.CreateChildKey("result"))
    || reply.ReceiverId != sender || strings.Decode(reply.Envelope) != "42")
{
    throw new InvalidOperationException("Typed reply identity, destination, or body was lost.");
}
var headerSerializer = services.GetRequiredService<Serializer>().GetSerializer<Dictionary<string, byte[]>>();

var text = strings.Create(new MessageId(key.Key.CreateChildKey("text")), receiver, "abc");
var textContext = new SmokeContext(text.ToInboxMessage());
await dispatcher.HandleAsync(textContext, CancellationToken.None);
if (!textContext.Completed) throw new InvalidOperationException("Typed struct-argument handler did not complete.");
var invalid = strings.Create(new MessageId(key.Key.CreateChildKey("invalid")), receiver, "");
var rejected = new SmokeContext(invalid.ToInboxMessage());
await dispatcher.HandleAsync(rejected, CancellationToken.None);
if (!rejected.Completed || rejected.DeadLetterReason != "Text is required." || outbox.Count != 1)
{
    throw new InvalidOperationException("Typed handler did not preserve its explicit permanent failure.");
}
outbox.Send(integers,
[
    (new MessageId(key.Key.CreateChildKey("batch-first")), sender, 8),
    (new MessageId(key.Key.CreateChildKey("batch-second")), sender, 9)
]);
if (outbox.Count != 3 || integers.Decode(outbox.Messages.Last().Envelope) != 9)
{
    throw new InvalidOperationException("Typed batch send failed.");
}
outbox.Send(integers, new MessageId(key.Key.CreateChildKey("send")), sender, 7);
if (integers.Decode(outbox.Messages.Last().Envelope) != 7) throw new InvalidOperationException("Typed send failed.");
var traceId = Guid.NewGuid();
var traceSerializer = services.GetRequiredService<Serializer>().GetSerializer<Guid>();
{
    var envelope = new Envelope
    {
        MessageId = new MessageId(key.Key.CreateChildKey("headers")),
        Payload = [],
        Headers = new Dictionary<string, byte[]>
        {
            ["trace-id"] = traceSerializer.SerializeToArray(traceId),
            ["attachment"] = [0, 255, 128]
        }
    };
    var decoded = envelope with { Headers = headerSerializer.Deserialize(headerSerializer.SerializeToArray(envelope.Headers)) };
    if (decoded.MessageId != envelope.MessageId || decoded.Payload.Length != 0
        || decoded.Headers is not { } headers || headers.ContainsKey("TRACE-ID") || !headers.TryGetValue("trace-id", out var traceBytes)
        || traceSerializer.Deserialize(traceBytes) != traceId
        || !headers.TryGetValue("attachment", out var attachment)
        || !attachment.AsSpan().SequenceEqual(new byte[] { 0, 255, 128 }))
    {
        throw new InvalidOperationException("Typed and raw headers did not round trip with an empty payload.");
    }
}
Console.WriteLine("NativeAOT typed sends, deterministic replies, static handlers, explicit permanent failures, and extensible headers passed.");

internal readonly record struct Offset(int Value);

[GenerateSerializerContext<int>]
[GenerateSerializerContext<string>]
[GenerateSerializerContext<GrainId>]
[GenerateSerializerContext<Guid>]
[GenerateSerializerContext<byte[]>]
[GenerateSerializerContext<Dictionary<string, byte[]>>]
internal partial class MessagingSerializerContext : SerializerContext;

internal sealed class SmokeState(SmokeOutbox outbox, MessageType<string> replyType, GrainId replyDestination)
{
    public SmokeOutbox Outbox { get; } = outbox;
    public MessageType<string> ReplyType { get; } = replyType;
    public GrainId ReplyDestination { get; } = replyDestination;
    public int Value { get; set; }
}

internal sealed class SmokeInbox(InboxDispatcher dispatcher) : IInbox
{
    public IInbox Register(IInboxHandler handler)
    {
        dispatcher.Add(handler);
        return this;
    }

    public IInbox Register(IInboxMessageSink sink) => throw new NotSupportedException();

    public IInbox Register(IInboxWorkSource source) => throw new NotSupportedException();
}

internal sealed class SmokeContext(InboxMessage envelope) : IInboxHandlerContext
{
    public InboxMessage Message { get; } = envelope;
    public bool Completed { get; private set; }
    public string? DeadLetterReason { get; private set; }
    public void Complete() => Completed = true;
    public void Fail(string reason)
    {
        DeadLetterReason = reason;
        Completed = true;
    }
}

internal sealed class SmokeOutbox : IOutbox
{
    private readonly List<OutboxMessage> _messages = [];
    public int Count => _messages.Count;
    public IEnumerable<OutboxMessage> Messages => _messages;
    public void Send(OutboxMessage envelope) => _messages.Add(envelope);
    public void Send(MessageId messageId) => throw new NotSupportedException();
    public IOutbox Register(IOutboxMessageSource source) => throw new NotSupportedException();
}
