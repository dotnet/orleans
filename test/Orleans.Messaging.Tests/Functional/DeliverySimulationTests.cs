using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging.Tests.Support;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

/// <summary>
/// Runs generated schedules of sends, delivery faults, crashes, and time against one sender and one receiver, and checks
/// the end-to-end delivery contract: no fault ever applies a message's effect twice, and once faults stop, every message
/// is processed exactly once in effect or, if its retries outlived <c>OutboxRetryTimeout</c>, dead-lettered at the sender.
/// </summary>
[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class DeliverySimulationTests : MessagingBehaviorTestBase
{
    private const string Route = "messages/record";
    private static readonly TimeSpan RetryInterval = FaultInjectionClusterFixture.RetryInterval;
    private readonly FaultInjectionClusterFixture _cluster;

    private static readonly Gen<Step> GenStep = Gen.Frequency(
        (4, Gen.Const(new Step(StepKind.Send))),
        (3, Gen.OneOfConst(DeliveryFaultKind.Throw, DeliveryFaultKind.TimeOut, DeliveryFaultKind.LoseResponse, DeliveryFaultKind.TimeOutInFlight)
            .Select(static fault => new Step(StepKind.Fault, fault))),
        (1, Gen.Const(new Step(StepKind.CrashSender))),
        (1, Gen.Const(new Step(StepKind.CrashReceiver))),
        (2, Gen.Const(new Step(StepKind.Advance))));

    public DeliverySimulationTests() : this(new FaultInjectionClusterFixture())
    {
    }

    private DeliverySimulationTests(FaultInjectionClusterFixture cluster) : base(cluster) => _cluster = cluster;

    [Fact]
    public Task GeneratedFaultSchedules_ApplyEachMessageOnceAndDeliverItOnceFaultsStop() =>
        GenStep.Array[1, 12].SampleAsync(RunScheduleAsync, iter: 6, threads: 1);

    private async Task RunScheduleAsync(Step[] schedule)
    {
        var sender = NewGrain();
        var receiver = NewGrain();
        var sent = new List<(MessageId Id, TestMessage Message)>();
        foreach (var step in schedule)
        {
            switch (step.Kind)
            {
                case StepKind.Send:
                    var message = NewMessage(sent.Count, "simulated");
                    sent.Add((await sender.SendAsync(receiver.GetGrainId(), Route, message), message));
                    break;
                case StepKind.Fault:
                    _cluster.Deliveries.Enqueue(Create(step.Fault));
                    break;
                case StepKind.CrashSender:
                    if (Fixture.Cluster.TryGetGrainContext(sender.GetGrainId(), out _)) await _cluster.CrashAsync(sender);
                    break;
                case StepKind.CrashReceiver:
                    if (Fixture.Cluster.TryGetGrainContext(receiver.GetGrainId(), out _)) await _cluster.CrashAsync(receiver);
                    break;
                case StepKind.Advance:
                    _ = await sender.GetSnapshotAsync();
                    await _cluster.AdvanceAndRunPumpAsync(sender, RetryInterval);
                    break;
            }

            // Safety holds during faults: no message's effect is ever applied twice.
            Assert.All((await receiver.GetSnapshotAsync()).Effects, static effect => Assert.Equal(1, effect.Count));
        }

        // Faults stop; each retry becomes due and succeeds, unless the message's retry timeout elapsed first.
        _cluster.Deliveries.Clear();
        for (var round = 0; round < 8; round++)
        {
            var snapshot = await sender.GetSnapshotAsync();
            if (snapshot.OutboxCount == 0) break;
            await _cluster.AdvanceAndRunPumpAsync(sender, RetryInterval);
            var outbox = Fixture.GetGrainContext(sender).ActivationServices.GetRequiredService<Outbox>();
            await Fixture.WaitUntilAsync(sender, () => !outbox.IsDelivering);
        }

        var final = await sender.GetSnapshotAsync();
        Assert.Equal(0, final.OutboxCount);
        var deadLettered = final.OutboxDeadLetters.Select(static letter => letter.MessageId).ToHashSet();
        var received = await Fixture.WaitForInboxCountAsync(receiver, 0);
        Assert.Empty(received.InboxDeadLetters);
        foreach (var (id, message) in sent)
        {
            var count = received.Effects.Where(effect => effect.LogicalId == message.LogicalId).Sum(static effect => effect.Count);
            if (deadLettered.Contains(id)) Assert.InRange(count, 0, 1);
            else Assert.Equal(1, count);
        }
    }

    private static DeliveryFault Create(DeliveryFaultKind kind) => kind switch
    {
        DeliveryFaultKind.Throw => DeliveryFault.Throw(),
        DeliveryFaultKind.TimeOut => DeliveryFault.TimeOut(),
        DeliveryFaultKind.LoseResponse => DeliveryFault.LoseResponse(),
        _ => DeliveryFault.TimeOutInFlight()
    };

    private enum StepKind { Send, Fault, CrashSender, CrashReceiver, Advance }

    private readonly record struct Step(StepKind Kind, DeliveryFaultKind Fault = default);
}