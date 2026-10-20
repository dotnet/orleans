using System.Reflection;
using Orleans.CodeGeneration;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Verifies that a stored payload which can't be decoded, and a result which can't be stored, affect only their own task:
/// the grain activates, runs its other tasks, and keeps committing.
/// </summary>
/// <remarks>The tests run the runtime on the grain's journal, so every payload is Orleans-serialized as in production.</remarks>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskStoredPayloadTests
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public Task RequestWhichNoLongerDecodesIsQuarantinedUntilCodeDecodesIt(string journalFormatKey) => ActivationScheduler.RunAsync(async () =>
    {
        PoisonedRequest.DecodeFails = false;
        PoisonedRequest.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new DurableTaskReplayHarness(static _ => DurableTask.FromResult(5), journalFormatKey);
        var poisonedId = TaskId.CreateRoot($"poisoned-{Guid.NewGuid():N}");
        var otherId = TaskId.CreateRoot("other");
        byte[] payload;
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await ((IDurableTaskServer)activation.Runtime).ScheduleAsync(
                poisonedId,
                new PoisonedRequest
                {
                    Context = new DurableTaskRequestContext { TargetId = DurableTaskReplayHarness.GrainId },
                    Poison = new PoisonValue(7),
                },
                TestCancellation);
            Assert.True(activation.Storage.TryGetTask(poisonedId, out var stored));
            payload = ((DurableTaskState)stored).RequestPayload!;
        }

        // A deployment can no longer decode the stored request: the grain activates and runs its other work, and the
        // task stalls with its stored request unchanged.
        PoisonedRequest.DecodeFails = true;
        using (var stalls = new StallRecorder(poisonedId))
        {
            await using var activation = await harness.ActivateAsync(TestCancellation);
            var stalled = await stalls.Stalled.WaitAsync(TimeSpan.FromSeconds(5), TestCancellation);
            Assert.Equal(DurableTaskAttemptCancellationReason.Diverged, stalled.Reason);
            Assert.Contains("no longer decodes", stalled.Exception.Message, StringComparison.Ordinal);

            await activation.ScheduleAsync(otherId, TestCancellation);
            Assert.Equal(5, (await activation.GetResponseAsync(otherId, TestCancellation)).GetResult<int>());
            Assert.True(activation.Storage.TryGetTask(poisonedId, out var quarantined));
            Assert.Null(quarantined.CompletedAt);
            Assert.Equal(payload, ((DurableTaskState)quarantined).RequestPayload);
            Assert.Equal(
                DurableTaskStatus.Pending,
                (await activation.Runtime.SubscribeOrPollAsync(poisonedId, new SubscribeOrPollOptions { PollTimeout = TimeSpan.Zero }, TestCancellation)).Status);
        }

        // Code which decodes the request runs the task from the stored request.
        PoisonedRequest.DecodeFails = false;
        PoisonedRequest.Release.SetResult();
        await using var recovered = await harness.ActivateAsync(TestCancellation);
        Assert.Equal(7, (await recovered.GetResponseAsync(poisonedId, TestCancellation)).GetResult<int>());
        Assert.Equal(5, (await recovered.GetResponseAsync(otherId, TestCancellation)).GetResult<int>());
    });

    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public Task ResultWhichDoesNotSerializeFaultsItsTaskAndLaterWorkCommits(string journalFormatKey) => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static _ => DurableTask.FromResult(new NoCodecValue()), journalFormatKey);
        await AssertFaultsAndLaterWorkCommitsAsync(harness, "result", typeof(NoCodecValue));
    });

    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public Task ExceptionWhichDoesNotSerializeFaultsItsTaskAndLaterWorkCommits(string journalFormatKey) => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static _ => DurableTask.Run(static _ => ThrowUnserializable()), journalFormatKey);
        await AssertFaultsAndLaterWorkCommitsAsync(harness, "exception", typeof(InvalidOperationException));

        static int ThrowUnserializable() =>
            throw new InvalidOperationException("The order was rejected.") { Data = { ["order"] = new NoCodecValue() } };
    });

    /// <summary>
    /// Runs the harness's workflow, which completes with an outcome that doesn't serialize, and asserts that its task
    /// faults durably while the grain commits later work.
    /// </summary>
    private static async Task AssertFaultsAndLaterWorkCommitsAsync(DurableTaskReplayHarness harness, string kind, Type type)
    {
        var faultedId = TaskId.CreateRoot("faulted");
        var laterId = TaskId.CreateRoot("later");
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(faultedId, TestCancellation);
            AssertSerializationFault(await activation.GetResponseAsync(faultedId, TestCancellation));

            harness.Workflow = static _ => DurableTask.FromResult(5);
            await activation.ScheduleAsync(laterId, TestCancellation);
            Assert.Equal(5, (await activation.GetResponseAsync(laterId, TestCancellation)).GetResult<int>());
        }

        // The fault committed like any result, so the next activation recovers it without running the workflow again.
        await using var recovered = await harness.ActivateAsync(TestCancellation);
        AssertSerializationFault(await recovered.GetResponseAsync(faultedId, TestCancellation));
        Assert.Equal(5, (await recovered.GetResponseAsync(laterId, TestCancellation)).GetResult<int>());

        void AssertSerializationFault(DurableTaskResponse response)
        {
            Assert.Equal(DurableTaskStatus.Failed, response.Status);
            var exception = Assert.IsType<SerializerException>(response.Exception);
            Assert.Contains($"The {kind} of durable task '{faultedId}', of type '{type}', can't be stored because it doesn't serialize", exception.Message, StringComparison.Ordinal);
        }
    }
}

/// <summary>A value of a type which has no codec, so it doesn't serialize.</summary>
internal sealed class NoCodecValue;

/// <summary>A value whose decoding fails while <see cref="PoisonedRequest.DecodeFails"/> is set, as a removed type's does.</summary>
internal sealed class PoisonValue(int value)
{
    public int Value { get; } = value;
}

[GenerateSerializer]
internal struct PoisonValueSurrogate
{
    [Id(0)]
    public int Value;
}

[RegisterConverter]
internal sealed class PoisonValueConverter : IConverter<PoisonValue, PoisonValueSurrogate>
{
    public PoisonValue ConvertFromSurrogate(in PoisonValueSurrogate surrogate) =>
        PoisonedRequest.DecodeFails ? throw new InvalidOperationException("The poisoned value's type was removed.") : new(surrogate.Value);

    public PoisonValueSurrogate ConvertToSurrogate(in PoisonValue value) => new() { Value = value.Value };
}

/// <summary>A request whose argument decodes only while <see cref="DecodeFails"/> is clear, and whose body waits for <see cref="Release"/>.</summary>
[GenerateSerializer]
[Alias("Microsoft.Orleans.DurableTasks.Tests.PoisonedRequest")]
internal sealed class PoisonedRequest : IDurableTaskRequest
{
    /// <summary>Gets or sets whether decoding the request fails. Only <see cref="DurableTaskStoredPayloadTests"/> uses it.</summary>
    public static bool DecodeFails { get; set; }

    /// <summary>Gets or sets the gate which the request's body waits for.</summary>
    public static TaskCompletionSource Release { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Id(0)]
    public DurableTaskRequestContext? Context { get; set; }

    [Id(1)]
    public PoisonValue? Poison { get; set; }

    [Id(2)]
    public InvokeMethodOptions Options { get; private set; }

    public void AddInvokeMethodOptions(InvokeMethodOptions options) => Options |= options;

    public object? GetTarget() => null;

    public void SetTarget(ITargetHolder holder)
    {
    }

    public ValueTask<Response> Invoke() => throw new NotSupportedException("Durable task requests are not invoked directly.");

    public int GetArgumentCount() => 0;

    public object? GetArgument(int index) => throw new ArgumentOutOfRangeException(nameof(index));

    public void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(nameof(index));

    public string GetMethodName() => "Run";

    public string GetInterfaceName() => "Microsoft.Orleans.DurableTasks.Tests.IPoisonedGrain";

    public string GetActivityName() => "Microsoft.Orleans.DurableTasks.Tests.IPoisonedGrain/Run";

    public MethodInfo GetMethod() => typeof(PoisonedRequest).GetMethod(nameof(GetMethod))!;

    public Type GetInterfaceType() => typeof(PoisonedRequest);

    public void Dispose()
    {
    }

    public DurableTask CreateTask()
    {
        var value = Poison!.Value;
        return DurableTask.Run(async cancellationToken =>
        {
            await Release.Task.WaitAsync(cancellationToken);
            return value;
        });
    }
}
