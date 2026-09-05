using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DurableEnvelopeContractTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly SerializerSessionPool _sessions;

    public DurableEnvelopeContractTests()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        _services = services.BuildServiceProvider();
        _sessions = _services.GetRequiredService<SerializerSessionPool>();
    }

    [Fact]
    public void EnvelopeBuilder_Complete_RoundTripsAllEnvelopeFieldsIncludingGeneralReplyTo()
    {
        var sender = GrainId.Create("sender", "17");
        var receiver = GrainId.Create("receiver", "23");
        var replyTo = GrainId.Create("audit", "general-reply");
        var correlation = HierarchicalKey.Create("orders/2026/42");
        var before = DateTimeOffset.UtcNow;

        var envelope = new DurableEnvelopeBuilder(_sessions, sender)
            .WithContextValue("tenant", "northwind")
            .To(receiver, "orders/submit")
            .WithReplyTo(replyTo)
            .WithCorrelationKey(correlation)
            .WithBody(new TestMessage(42, "ship"))
            .WithContextValue("attempt", 3)
            .Build();

        Assert.NotEqual(Guid.Empty, envelope.MessageId);
        Assert.Equal(sender, envelope.SenderId);
        Assert.Equal(receiver, envelope.ReceiverId);
        Assert.Equal("orders/submit", envelope.RouteKey);
        Assert.Equal(correlation, envelope.CorrelationKey);
        Assert.Equal(replyTo, envelope.ReplyTo);
        Assert.InRange(envelope.CreatedAt, before, DateTimeOffset.UtcNow);
        Assert.True(envelope.Data.TryGetBody<TestMessage>(out var body));
        Assert.Equal(new TestMessage(42, "ship"), body);
        Assert.True(envelope.Data.TryGetContextValue<string>("tenant", out var tenant));
        Assert.Equal("northwind", tenant);
        Assert.True(envelope.Data.TryGetContextValue<int>("attempt", out var attempt));
        Assert.Equal(3, attempt);
        Assert.Equal(["attempt", "tenant"], envelope.Data.ContextKeys.Order());

    }

    [Fact]
    public void EnvelopeBuilder_MissingRequiredField_ThrowsWithoutProducingEnvelope()
    {
        var sender = GrainId.Create("sender", "missing");
        var receiver = GrainId.Create("receiver", "missing");

        var missingBody = new DurableEnvelopeBuilder(_sessions, sender).To(receiver, "route");
        var missingTarget = new DurableEnvelopeBuilder(_sessions, sender).WithBody("payload");

        var bodyError = Assert.Throws<InvalidOperationException>(() => missingBody.Build());
        var targetError = Assert.Throws<InvalidOperationException>(() => missingTarget.Build());
        Assert.Contains("body", bodyError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("route", targetError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnvelopeBuilder_DuplicateBodyAndContextKey_RejectsAmbiguousMetadata()
    {
        var builder = new DurableEnvelopeBuilder(_sessions, GrainId.Create("sender", "duplicate"))
            .To(GrainId.Create("receiver", "duplicate"), "route")
            .WithBody("first")
            .WithContextValue("trace", "one");

        var bodyError = Assert.Throws<InvalidOperationException>(() => builder.WithBody("second"));
        var contextError = Assert.Throws<InvalidOperationException>(() => builder.WithContextValue("trace", "two"));
        Assert.Contains("already", bodyError.Message, StringComparison.Ordinal);
        Assert.Contains("trace", contextError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvelopeBuilder_AfterBuild_RejectsEveryMutation()
    {
        var builder = new DurableEnvelopeBuilder(_sessions, GrainId.Create("sender", "built"))
            .To(GrainId.Create("receiver", "built"), "route")
            .WithBody("payload");
        _ = builder.Build();

        Assert.Throws<InvalidOperationException>(() => builder.To(GrainId.Create("receiver", "other"), "other"));
        Assert.Throws<InvalidOperationException>(() => builder.WithBody("other"));
        Assert.Throws<InvalidOperationException>(() => builder.WithContextValue("key", "value"));
        Assert.Throws<InvalidOperationException>(() => builder.WithCorrelationKey("correlation"));
        Assert.Throws<InvalidOperationException>(() => builder.WithReplyTo(GrainId.Create("reply", "other")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EnvelopeBuilder_InvalidRoute_Throws(string? route)
    {
        var builder = new DurableEnvelopeBuilder(_sessions, GrainId.Create("sender", "route"));
        Assert.ThrowsAny<ArgumentException>(() => builder.To(GrainId.Create("receiver", "route"), route!));
    }

    [Fact]
    public void EnvelopeBuilder_DefaultDestination_ThrowsAtConfiguration()
    {
        var targetBuilder = new DurableEnvelopeBuilder(_sessions, GrainId.Create("sender", "target"));
        var replyBuilder = new DurableEnvelopeBuilder(_sessions, GrainId.Create("sender", "reply"));

        var targetException = Assert.Throws<ArgumentException>(() => targetBuilder.To(default, "route"));
        var replyException = Assert.Throws<ArgumentException>(() => replyBuilder.WithReplyTo(default));

        Assert.Equal("target", targetException.ParamName);
        Assert.Equal("replyTo", replyException.ParamName);
    }

    [Fact]
    public void EnvelopeData_WrongBodyOrContextType_FailsWithoutCorruptingOtherValues()
    {
        var envelope = new DurableEnvelopeBuilder(_sessions, GrainId.Create("sender", "types"))
            .To(GrainId.Create("receiver", "types"), "types")
            .WithContextValue("count", 7)
            .WithContextValue("label", "valid")
            .WithBody(new TestMessage(9, "body"))
            .Build();

        Assert.False(envelope.Data.TryGetBody<string>(out var wrongBody));
        Assert.Null(wrongBody);
        Assert.False(envelope.Data.TryGetContextValue<Guid>("count", out var wrongContext));
        Assert.Equal(Guid.Empty, wrongContext);
        Assert.True(envelope.Data.TryGetBody<TestMessage>(out var body));
        Assert.NotNull(body);
        Assert.Equal(9, body.Id);
        Assert.True(envelope.Data.TryGetContextValue<string>("label", out var label));
        Assert.Equal("valid", label);
        Assert.True(envelope.Data.GetBodyBytes().Length > 0);
        Assert.True(envelope.Data.TryGetContextBytes("count", out var rawCount));
        Assert.True(rawCount.Length > 0);
        Assert.False(envelope.Data.TryGetContextBytes("absent", out var absent));
        Assert.True(absent.IsEmpty);

    }

    [Fact]
    public void EnvelopeData_DeepCopyOwnsContextIndexMap()
    {
        var envelope = new DurableEnvelopeBuilder(_sessions, GrainId.Create("sender", "copy"))
            .To(GrainId.Create("receiver", "copy"), "copy")
            .WithContextValue("tenant", "northwind")
            .WithBody(new TestMessage(42, "copy"))
            .Build();
        var copy = _services.GetRequiredService<DeepCopier>().Copy(envelope);
        var field = typeof(DurableEnvelopeData).GetField(
            "_contextIndices",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalIndices = Assert.IsType<Dictionary<string, (int Offset, int Length)>>(
            field.GetValue(envelope.Data));
        var copiedIndices = Assert.IsType<Dictionary<string, (int Offset, int Length)>>(
            field.GetValue(copy.Data));

        Assert.NotSame(originalIndices, copiedIndices);
        originalIndices.Add("mutated-after-copy", default);
        Assert.False(copy.Data.HasContextKey("mutated-after-copy"));
    }

    [Fact]
    public void EnvelopeSerializer_RoundTripsCorrelationReplyBodyAndContext()
    {
        var serializer = _services.GetRequiredService<Serializer<DurableEnvelope>>();
        var correlation = HierarchicalKey.Create("orders/42/dispatch");
        var replyTo = GrainId.Create("audit", "42");
        var envelope = new DurableEnvelopeBuilder(_sessions, GrainId.Create("sender", "42"))
            .To(GrainId.Create("receiver", "42"), "orders/dispatch")
            .WithCorrelationKey(correlation)
            .WithReplyTo(replyTo)
            .WithContextValue("tenant", "northwind")
            .WithBody(new TestMessage(42, "dispatch"))
            .Build();

        var copy = serializer.Deserialize(serializer.SerializeToArray(envelope));

        Assert.Equal(envelope.MessageId, copy.MessageId);
        Assert.Equal(correlation, copy.CorrelationKey);
        Assert.Equal(replyTo, copy.ReplyTo);
        Assert.True(copy.Data.TryGetBody<TestMessage>(out var body));
        Assert.Equal(new TestMessage(42, "dispatch"), body);
        Assert.True(copy.Data.TryGetContextValue<string>("tenant", out var tenant));
        Assert.Equal("northwind", tenant);
    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public void TypedReadsRejectWireCompatibleDeclaredTypeMismatches()
    {
        var envelope = new DurableEnvelopeBuilder(
                _sessions,
                GrainId.Create("sender", "typed-mismatch"))
            .To(GrainId.Create("receiver", "typed-mismatch"), "typed/mismatch")
            .WithBody(42)
            .WithContextValue("attempt", 7)
            .Build();

        Assert.True(envelope.Data.TryGetBody<int>(out var body));
        Assert.Equal(42, body);
        Assert.False(envelope.Data.TryGetBody<uint>(out _));
        Assert.True(envelope.Data.TryGetContextValue<int>("attempt", out var attempt));
        Assert.Equal(7, attempt);
        Assert.False(envelope.Data.TryGetContextValue<uint>("attempt", out _));
    }

    [Fact]
    public void DeclaredTypeMetadataRoundTripsWithEnvelope()
    {
        var envelope = new DurableEnvelopeBuilder(
                _sessions,
                GrainId.Create("sender", "typed-roundtrip"))
            .To(GrainId.Create("receiver", "typed-roundtrip"), "typed/roundtrip")
            .WithBody(42)
            .WithContextValue("attempt", 7)
            .Build();
        var serializer = _services.GetRequiredService<Serializer<DurableEnvelope>>();

        var copy = serializer.Deserialize(serializer.SerializeToArray(envelope));

        Assert.True(copy.Data.TryGetBody<int>(out var body));
        Assert.Equal(42, body);
        Assert.False(copy.Data.TryGetBody<uint>(out _));
        Assert.True(copy.Data.TryGetContextValue<int>("attempt", out var attempt));
        Assert.Equal(7, attempt);
        Assert.False(copy.Data.TryGetContextValue<uint>("attempt", out _));
    }

    [Fact]
    public void OutboxEquivalenceIncludesDeclaredTypeMetadata()
    {
        var sender = GrainId.Create("sender", "typed-equivalence");
        var receiver = GrainId.Create("receiver", "typed-equivalence");
        var first = new DurableEnvelopeBuilder(_sessions, sender)
            .To(receiver, "typed/equivalence")
            .WithBody(0)
            .Build();
        var secondTemplate = new DurableEnvelopeBuilder(_sessions, sender)
            .To(receiver, "typed/equivalence")
            .WithBody(0U)
            .Build();
        var second = new DurableEnvelope
        {
            MessageId = first.MessageId,
            SenderId = secondTemplate.SenderId,
            ReceiverId = secondTemplate.ReceiverId,
            RouteKey = secondTemplate.RouteKey,
            CorrelationKey = secondTemplate.CorrelationKey,
            ReplyTo = secondTemplate.ReplyTo,
            Data = secondTemplate.Data,
            CreatedAt = first.CreatedAt,
        };

        Assert.False(DurableOutbox.AreEquivalent(first, second));
    }

    [Fact]
    public void OutboxEquivalenceIgnoresCreationTimestamp()
    {
        var sender = GrainId.Create("sender", "timestamp-equivalence");
        var receiver = GrainId.Create("receiver", "timestamp-equivalence");
        var first = new DurableEnvelopeBuilder(_sessions, sender)
            .To(receiver, "timestamp/equivalence")
            .WithBody(42)
            .Build();
        var second = new DurableEnvelope
        {
            MessageId = first.MessageId,
            SenderId = first.SenderId,
            ReceiverId = first.ReceiverId,
            RouteKey = first.RouteKey,
            CorrelationKey = first.CorrelationKey,
            ReplyTo = first.ReplyTo,
            Data = first.Data,
            CreatedAt = first.CreatedAt.AddMinutes(1),
        };

        Assert.True(DurableOutbox.AreEquivalent(first, second));
    }

    [GenerateSerializer, Immutable]
    public sealed record TestMessage([property: Id(0)] int Id, [property: Id(1)] string Action);
}
