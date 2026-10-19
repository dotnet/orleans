using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Connections;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization.Session;
using Orleans.Serialization.TestKit;
using Xunit;

namespace Orleans.Serialization.UnitTests;

/// <summary>
/// Runs the codec tester's checks for the codec which carries messages within other messages, as requests travel with a migrating activation.
/// </summary>
/// <remarks>
/// <see cref="Message"/> is internal, so its tester cannot be a public test class. This class runs each of the tester's checks.
/// </remarks>
[Trait("Category", "BVT")]
[Trait("Suite", "BVT")]
[Trait("Provider", "None")]
[Trait("Area", "Serialization")]
public sealed class MessageCodecTests(ITestOutputHelper output, SerializationTesterFixture fixture) : IClassFixture<SerializationTesterFixture>
{
    public static TheoryData<string> Checks { get; } = new(
        typeof(FieldCodecTester<Message, MessageCodec>)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.GetCustomAttribute<FactAttribute>() is not null)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Checks))]
    public void MessageCodec_PassesCodecCheck(string check)
    {
        var tester = new MessageCodecTester(output, fixture);
        try
        {
            typeof(MessageCodecTester).GetMethod(check, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)!.Invoke(tester, null);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        }
    }
}

internal sealed class MessageCodecTester(ITestOutputHelper output, SerializationTesterFixture fixture) : FieldCodecTester<Message, MessageCodec>(output, fixture)
{
    protected override int[] MaxSegmentSizes => [1024];

    protected override void Configure(ISerializerBuilder builder)
    {
        builder.Services.AddMetrics();
        builder.Services.AddSingleton(services =>
        {
            var instruments = new OrleansInstruments(services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
            var messaging = new MessagingInstruments(instruments);
            var trace = new MessagingTrace(NullLoggerFactory.Instance, messaging, new MessagingProcessingInstruments(instruments));
            var sessions = services.GetRequiredService<SerializerSessionPool>();

            // The codec serializes messages without routing them, so it needs no message center.
            return new MessageHandlerShared(
                trace,
                new ConnectionTrace(NullLoggerFactory.Instance),
                () => new MessageSerializer(sessions, new SiloMessagingOptions()),
                new MessageFactory(services.GetRequiredService<DeepCopier>(), NullLogger<MessageFactory>.Instance, trace),
                messageCenter: null!,
                messaging);
        });
    }

    protected override Message CreateValue() => CreateRequest(Random.Next(), $"body-{Random.Next()}");

    protected override Message[] TestValues =>
    [
        null!,
        CreateRequest(1, body: null),
        CreateRequest(2, 42),
        CreateRequest(3, "value", new() { ["key"] = "context" }),
    ];

    protected override bool Equals(Message? left, Message? right)
    {
        if (left is null || right is null)
        {
            return ReferenceEquals(left, right);
        }

        return left.Id == right.Id
            && left.Direction == right.Direction
            && left.SendingGrain == right.SendingGrain
            && left.TargetGrain == right.TargetGrain
            && Equals(left.BodyObject, right.BodyObject)
            && (left.RequestContextData ?? []).OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .SequenceEqual((right.RequestContextData ?? []).OrderBy(entry => entry.Key, StringComparer.Ordinal));
    }

    // A message's serialized time to live is the time remaining, which changes between writes, so these messages have none. MessageFramesTests covers it.
    private static Message CreateRequest(int id, object? body, Dictionary<string, object>? requestContext = null) => new()
    {
        Direction = Message.Directions.Request,
        Id = new CorrelationId(id),
        SendingGrain = GrainId.Create("test", "caller"),
        TargetGrain = GrainId.Create("test", $"target-{id}"),
        BodyObject = body,
        RequestContextData = requestContext,
    };
}
