using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Core;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Cross-version recovery test: feeds a pre-canned journal blob captured from <c>upstream/main</c>
/// (using the legacy <c>StateMachineManager</c> + inlined <c>Durable*</c> writers) through the PR's
/// <see cref="JournaledStateManager"/> wired with the OrleansBinary codecs, and asserts the recovered
/// in-memory state matches what main wrote.
/// </summary>
/// <remarks>
/// The hex blob in <see cref="UpstreamMainJournalHex"/> was produced by the
/// <c>CaptureCrossVersionRecoveryFixture.EmitMainJournalHex</c> test running against
/// upstream/main (<c>5989958561</c>). The capture exercises every <c>Durable*</c> type and pins the
/// stream-registration order so the journal stream IDs (8..14) line up with the registrations below.
/// To regenerate after an intentional wire-format change to the OrleansBinary codecs, re-run that
/// fixture in a worktree pinned to upstream/main and paste the new hex here.
/// </remarks>
[TestCategory("BVT")]
public sealed class UpstreamMainCompatibilityTests : JournalingTestBase
{
    private const string UpstreamMainJournalHex =
        "1701000140096469637401111701000140096C697374011319010001400B71756575650115" +
        "150100014007736574011719010001400B76616C7565011919010001400B7374617465011B" +
        "150100014007746373011D19110001400B616C706861010517110001400962657461010915" +
        "130001400B666972737417130001400D7365636F6E6415130001400B74686972640B150001" +
        "00290B1500010051111700014007666F6F1117000140076261720B19000100A9171B000140" +
        "0B68656C6C6F010D1D0001001A03";

    [Fact]
    public async Task OrleansBinary_RecoversUpstreamMainWrittenJournal()
    {
        var storage = new VolatileJournalStorage();
        await storage.AppendAsync(new System.Buffers.ReadOnlySequence<byte>(Convert.FromHexString(UpstreamMainJournalHex)), default);

        var states = CreateStates(storage);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await states.Manager.InitializeAsync(cts.Token);

        Assert.Equal(2, states.Dictionary.Count);
        Assert.Equal(1, states.Dictionary["alpha"]);
        Assert.Equal(2, states.Dictionary["beta"]);

        Assert.Equal(["first", "second", "third"], states.List);

        Assert.Equal(2, states.Queue.Count);
        Assert.Equal(10, states.Queue.Dequeue());
        Assert.Equal(20, states.Queue.Dequeue());

        Assert.True(states.Set.SetEquals(["foo", "bar"]));

        Assert.Equal(42, states.Value.Value);

        Assert.Equal("hello", ((IStorage<string>)states.State).State);

        Assert.Equal(DurableTaskCompletionSourceStatus.Completed, states.Tcs.State.Status);
        Assert.Equal(99, states.Tcs.State.Value);
        Assert.Equal(99, await states.Tcs.Task);
    }

    private DurableStates CreateStates(IJournalStorage storage)
    {
        var manager = CreateManager(storage);
        // Registration order MUST match the capture fixture so that journal stream IDs (8..14)
        // line up with what upstream/main wrote.
        return new DurableStates(
            manager,
            new DurableDictionary<string, int>("dict", manager,
                new OrleansBinaryDictionaryOperationCodec<string, int>(ValueCodec<string>(), ValueCodec<int>(), SessionPool)),
            new DurableList<string>("list", manager,
                new OrleansBinaryListOperationCodec<string>(ValueCodec<string>(), SessionPool)),
            new DurableQueue<int>("queue", manager,
                new OrleansBinaryQueueOperationCodec<int>(ValueCodec<int>(), SessionPool)),
            new DurableSet<string>("set", manager,
                new OrleansBinarySetOperationCodec<string>(ValueCodec<string>(), SessionPool)),
            new DurableValue<int>("value", manager,
                new OrleansBinaryValueOperationCodec<int>(ValueCodec<int>(), SessionPool)),
            new DurableState<string>("state", manager,
                new OrleansBinaryStateOperationCodec<string>(ValueCodec<string>(), SessionPool)),
            new DurableTaskCompletionSource<int>(
                "tcs",
                manager,
                new OrleansBinaryTcsOperationCodec<int>(ValueCodec<int>(), ValueCodec<Exception>(), SessionPool),
                Copier<int>(),
                Copier<Exception>()));
    }

    private JournaledStateManager CreateManager(IJournalStorage storage)
        => new(
            storage,
            LoggerFactory.CreateLogger<JournaledStateManager>(),
            Options.Create(ManagerOptions),
            new OrleansBinaryDictionaryOperationCodec<string, ulong>(ValueCodec<string>(), ValueCodec<ulong>(), SessionPool),
            new OrleansBinaryDictionaryOperationCodec<string, DateTime>(ValueCodec<string>(), ValueCodec<DateTime>(), SessionPool),
            TimeProvider.System,
            new OrleansBinaryJournalFormat(SessionPool));

    private IJournalValueCodec<T> ValueCodec<T>() => new OrleansJournalValueCodec<T>(CodecProvider.GetCodec<T>(), SessionPool);

    private DeepCopier<T> Copier<T>() => ServiceProvider.GetRequiredService<DeepCopier<T>>();

    private sealed record DurableStates(
        JournaledStateManager Manager,
        DurableDictionary<string, int> Dictionary,
        DurableList<string> List,
        DurableQueue<int> Queue,
        DurableSet<string> Set,
        DurableValue<int> Value,
        DurableState<string> State,
        DurableTaskCompletionSource<int> Tcs);
}
