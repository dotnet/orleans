using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Journaling;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>
/// Verifies that durable grain RPC journals each request and result payload once in each grain's journal.
/// </summary>
/// <remarks>
/// The callee's inbox accepts the invocation directly into task state, and its outbox journals only a reference to the
/// completion, which it materializes from the stored result. The caller's inbox accepts the completion directly into
/// the caller's task state. Task state persists payloads separately from metadata, so metadata updates never rewrite
/// them. The JSON format writes byte arrays as base64 strings, so the counts also search decoded strings.
/// </remarks>
public abstract partial class DurableTaskPayloadJournalingTests(string journalFormatKey) : IAsyncLifetime
{
    private readonly DurableTaskRecoveryFixture _fixture = new(journalFormatKey);

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task RemoteCallJournalsEachPayloadOncePerGrainAndDeletesDeliveredResult()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var caller = _fixture.Client.GetGrain<IPayloadCallerGrain>(Guid.NewGuid());
        var worker = _fixture.Client.GetGrain<IPayloadWorkerGrain>(Guid.NewGuid());
        var seed = Random.Shared.Next();
        var input = PayloadMarkers.Input(seed);
        var output = PayloadMarkers.Transform(input);

        var scheduled = await caller.CallAsync(worker.GetPrimaryKey(), seed).ScheduleAsync("payload-call", cancellationToken);
        var response = await scheduled.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.True(response.GetResult<bool>());

        // The caller's inbox accepted the completion, so the callee's outbox retires it and deletes the task.
        var childId = TaskId.CreateRoot("payload-call").Child(PayloadCallerGrain.CallId);
        await WaitUntilDeletedAsync(worker, childId);

        var callerJournal = await ReadJournalAsync(caller.GetGrainId());
        var workerJournal = await ReadJournalAsync(worker.GetGrainId());
        Assert.Equal(1, CountPayload(callerJournal, input));
        Assert.Equal(1, CountPayload(workerJournal, input));
        Assert.Equal(1, CountPayload(workerJournal, output));
        Assert.Equal(1, CountPayload(callerJournal, output));
        Assert.DoesNotContain("complete-ack", Encoding.Latin1.GetString(callerJournal), StringComparison.Ordinal);
        Assert.DoesNotContain("complete-ack", Encoding.Latin1.GetString(workerJournal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientCallJournalsEachPayloadOnceAndRetainsResultForPolling()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var worker = _fixture.Client.GetGrain<IPayloadWorkerGrain>(Guid.NewGuid());
        var input = PayloadMarkers.Input(Random.Shared.Next());
        var output = PayloadMarkers.Transform(input);

        var scheduled = await worker.TransformAsync(input).ScheduleAsync("client-call", cancellationToken);
        var response = await scheduled.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal(output, response.GetResult<byte[]>());

        // A client has no inbox, so the result stays retained for polling after further writes.
        var other = await worker.TransformAsync(PayloadMarkers.Input(Random.Shared.Next())).ScheduleAsync("other-call", cancellationToken);
        await other.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        var polled = await worker.GetDurableTask<byte[]>("client-call").GetResponseAsync(cancellationToken);
        Assert.Equal(output, polled.GetResult<byte[]>());

        var workerJournal = await ReadJournalAsync(worker.GetGrainId());
        Assert.Equal(1, CountPayload(workerJournal, input));
        Assert.Equal(1, CountPayload(workerJournal, output));
    }

    private async Task WaitUntilDeletedAsync(IAddressable grain, TaskId taskId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var extension = grain.AsReference<IDurableTaskGrainExtension>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                var retained = await extension.SubscribeOrPollAsync(taskId, new SubscribeOrPollOptions { PollTimeout = TimeSpan.Zero }, cancellationToken);
                Assert.Equal(DurableTaskStatus.CompletedSuccessfully, retained.Status);
            }
            catch (DurableTaskNotFoundException)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Durable task '{taskId}' was not deleted after its completion was delivered.");
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    private async Task<byte[]> ReadJournalAsync(GrainId grainId)
    {
        var consumer = new JournalBytes();
        await _fixture.Storage.CreateStorage(JournalId.FromGrainId(grainId)).ReadAsync(consumer, TestContext.Current.CancellationToken);
        return consumer.ToArray();
    }

    private static int CountPayload(byte[] journal, byte[] payload)
    {
        var count = CountOccurrences(journal, payload);
        foreach (Match match in Base64String().Matches(Encoding.Latin1.GetString(journal)))
        {
            var buffer = new byte[match.Groups[1].Length];
            if (Convert.TryFromBase64String(match.Groups[1].Value, buffer, out var written))
            {
                count += CountOccurrences(buffer.AsSpan(0, written), payload);
            }
        }

        return count;
    }

    private static int CountOccurrences(ReadOnlySpan<byte> data, ReadOnlySpan<byte> value)
    {
        var count = 0;
        for (var index = data.IndexOf(value); index >= 0; index = data.IndexOf(value))
        {
            count++;
            data = data[(index + value.Length)..];
        }

        return count;
    }

    [GeneratedRegex("\"([A-Za-z0-9+/]{24,}={0,2})\"")]
    private static partial Regex Base64String();

    private sealed class JournalBytes : IJournalStorageConsumer
    {
        private readonly MemoryStream _bytes = new();

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            while (buffer.Length > 0)
            {
                var chunk = new byte[buffer.Length];
                buffer.Read(chunk);
                _bytes.Write(chunk);
            }
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}

/// <summary>Runs payload journaling coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskPayloadJournalingTests() : DurableTaskPayloadJournalingTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs payload journaling coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskPayloadJournalingTests() : DurableTaskPayloadJournalingTests(DurableTaskRecoveryFixture.OrleansBinary);

/// <summary>Derives distinctive, reproducible payloads, so a test can find each one in journal bytes.</summary>
public static class PayloadMarkers
{
    public static byte[] Input(int seed) => SHA512.HashData(BitConverter.GetBytes(seed));

    public static byte[] Transform(byte[] input) => SHA512.HashData(input);
}

public interface IPayloadCallerGrain : IGrainWithGuidKey
{
    DurableTask<bool> CallAsync(Guid workerKey, int seed);
}

public interface IPayloadWorkerGrain : IGrainWithGuidKey
{
    DurableTask<byte[]> TransformAsync(byte[] input);
}

[GrainType("durable-task-payload-caller-test")]
public sealed class PayloadCallerGrain : DurableGrain, IPayloadCallerGrain
{
    public const string CallId = "transform";

    // The caller derives the request payload and returns only whether the result matches, so its own root task
    // stores neither payload.
    public async DurableTask<bool> CallAsync(Guid workerKey, int seed)
    {
        var input = PayloadMarkers.Input(seed);
        var result = await GrainFactory.GetGrain<IPayloadWorkerGrain>(workerKey).TransformAsync(input).WithId(CallId);
        return result.AsSpan().SequenceEqual(PayloadMarkers.Transform(input));
    }
}

[GrainType("durable-task-payload-worker-test")]
public sealed class PayloadWorkerGrain : DurableGrain, IPayloadWorkerGrain
{
    public DurableTask<byte[]> TransformAsync(byte[] input) => DurableTask.FromResult(PayloadMarkers.Transform(input));
}
