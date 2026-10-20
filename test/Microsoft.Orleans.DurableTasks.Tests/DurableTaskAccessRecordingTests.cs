using NSubstitute;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Journaling;
using Orleans.Messaging;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Verifies which accesses a durable task body's records hold: the body's own, and none of the runtime's work which the
/// body's operations request.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskAccessRecordingTests
{
    private static readonly TaskId WorkflowId = TaskId.CreateRoot("workflow");
    private static readonly DurableSignal<int> Ready = new("ready");

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public static TheoryData<string> RuntimeOperations => new()
    {
        "post", "schedule", "await-child", "child-accesses", "delay", "handle-cancel", "handle-abort", "manager-cancel",
        "manager-abort", "signal-wait", "when-all", "when-any", "get-version", "run-inline",
    };

    [Theory]
    [MemberData(nameof(RuntimeOperations))]
    public Task RuntimeOperationFromABodyRecordsOnlyTheBodysAccesses(string operation) => ActivationScheduler.RunAsync(async () =>
    {
        IDurableTaskManager? manager = null;
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, manager!, operation));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            manager = activation.Runtime;
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;

            // The body's log holds its read and its write of the counter, around the operation, and nothing of the work
            // which the operation requested from the runtime.
            var counter = new JournaledStateAccessTarget("counter", Substitute.For<IJournaledStateManager>()).Id;
            Assert.Equal(
                [
                    (JournaledStateAccessKind.Read, JournaledStateOperation.ValueGet, counter),
                    (JournaledStateAccessKind.Write, JournaledStateOperation.ValueSet, counter),
                ],
                ParseHeaders(activation.Storage.GetAccessLog(WorkflowId)));
            Assert.Equal(1, activation.Grain.Counter.Value);
        }

        // The replay returns both records, so the increment applies once and the body completes without diverging.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        manager = replay.Runtime;
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        replay.Grain.OpenGate();
        Assert.Equal(0, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        Assert.Equal(1, replay.Grain.Counter.Value);

        static async DurableTask<int> Workflow(ReplayGrain grain, IDurableTaskManager manager, string operation)
        {
            var value = grain.Counter.Value;
            await RunAsync(grain, manager, operation);
            grain.Counter.Value = value + 1;
            await grain.WaitAtGateAsync();
            return value;
        }
    });

    private static async Task RunAsync(ReplayGrain grain, IDurableTaskManager manager, string operation)
    {
        var sibling = TaskId.CreateRoot("sibling");
        switch (operation)
        {
            case "post":
                _ = DurableTask.Run(static _ => { }).WithId("child").Post();
                break;
            case "schedule":
                await (await DurableTask.Run(static _ => { }).WithId("child").ScheduleAsync());
                break;
            case "await-child":
                await DurableTask.Run(static _ => { }).WithId("child");
                break;
            case "child-accesses":
                await DurableTask.Run(_ => grain.Log.Append(grain.Log.Count)).WithId("child");
                break;
            case "delay":
                _ = await DurableTask.Delay(TimeSpan.FromHours(1)).WithId("delay").ScheduleAsync();
                break;
            case "handle-cancel":
                var canceled = await DurableTask.Delay(TimeSpan.FromHours(1)).WithId("delay").ScheduleAsync();
                await canceled.CancelAsync();
                break;
            case "handle-abort":
                var aborted = await DurableTask.Delay(TimeSpan.FromHours(1)).WithId("delay").ScheduleAsync();
                await aborted.AbortAsync("not needed");
                break;
            case "manager-cancel":
                // The manager cancels a task which this grain retains, such as the body's own child.
                var managed = await DurableTask.Delay(TimeSpan.FromHours(1)).WithId("delay").ScheduleAsync();
                await manager.CancelAsync(managed.Id, "not needed");
                break;
            case "manager-abort":
                await manager.GetTask<int>(sibling).AbortAsync("not needed");
                break;
            case "signal-wait":
                await manager.SignalAsync(WorkflowId, Ready, 1);
                _ = await Ready.WaitAsync();
                break;
            case "when-all":
                await DurableTask.WhenAll([DurableTask.Run(static _ => { }), DurableTask.Run(static _ => { })]);
                break;
            case "when-any":
                await DurableTask.WhenAny([DurableTask.Run(static _ => { }), DurableTask.Run(static _ => { })]);
                break;
            case "get-version":
                _ = DurableTask.GetVersion("change", 0, 1);
                break;
            case "run-inline":
                // The effect's record covers its accesses, so the body's log holds none of them.
                DurableTask.RunInline("effect", () => grain.Log.Append(grain.Log.Count));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    [Fact]
    public void TransportSendsThroughTheOutboxAsTheJournaledStateHostsWork()
    {
        var host = new CountingHost();
        var outbox = new HostCheckingOutbox(host);
        var transport = new DurableTaskMessageTransport(new Lazy<IOutbox>(outbox), MessagingTestHelpers.MessageTypes, host);

        transport.Send(MessageId.Parse("reference", null));

        // The outbox's routing finds the host's work, so no body's context records the runtime's send.
        Assert.Equal([1], outbox.HostDepths);
        Assert.Equal(0, host.Depth);
    }

    private sealed class CountingHost : IJournaledStateContextHost
    {
        public int Depth { get; private set; }

        public JournaledStateContextProvider? ContextProvider { get; set; }

        public void EnterHostAccess() => Depth++;

        public void ExitHostAccess() => Depth--;
    }

    private sealed class HostCheckingOutbox(CountingHost host) : IOutbox
    {
        public List<int> HostDepths { get; } = [];

        public void Send(OutboxMessage message) => HostDepths.Add(host.Depth);

        public void Send(MessageId messageId) => HostDepths.Add(host.Depth);

        public bool Withdraw(MessageId messageId) => false;

        public IOutbox Register(IOutboxMessageSource source) => this;
    }

    /// <summary>Reads the kind, operation, and state of each record of an access log.</summary>
    /// <remarks>
    /// A record is <c>[kind | flags][operation][state:4]</c>, followed by <c>[hash:4]</c> when it carries an argument
    /// hash and by <c>[length:4][outcome]</c> when it has an outcome.
    /// </remarks>
    internal static List<(JournaledStateAccessKind Kind, JournaledStateOperation Operation, int Target)> ParseHeaders(ReadOnlySpan<byte> log)
    {
        const byte KindMask = 0x03;
        const byte HasValue = 0x04;
        const byte HasArgumentHash = 0x10;
        var records = new List<(JournaledStateAccessKind, JournaledStateOperation, int)>();
        while (!log.IsEmpty)
        {
            var flags = log[0];
            records.Add(((JournaledStateAccessKind)(flags & KindMask), (JournaledStateOperation)log[1], System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(log[2..])));
            log = log[6..];
            if ((flags & HasArgumentHash) != 0)
            {
                log = log[4..];
            }

            if ((flags & HasValue) != 0)
            {
                log = log[(4 + System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(log))..];
            }
        }

        return records;
    }
}
