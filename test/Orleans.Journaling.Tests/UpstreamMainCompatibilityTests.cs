using System.Buffers;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Core;
using Orleans.Serialization;
using Orleans.Serialization.Codecs;
using Xunit;
using static VerifyXunit.Verifier;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Cross-version recovery test: feeds a pre-canned journal blob captured from <c>upstream/main</c>
/// (using the legacy <c>StateMachineManager</c> + inlined <c>Durable*</c> writers) through the PR's
/// <see cref="JournaledStateManager"/> wired with the OrleansBinary codecs, and asserts the recovered
/// in-memory state matches what main wrote.
/// </summary>
/// <remarks>
/// The binary fixture under <c>fixtures</c> was produced by the
/// <c>CaptureCrossVersionRecoveryFixture.EmitMainJournalHex</c> test running against
/// upstream/main (<c>5989958561</c>). The capture exercises every <c>Durable*</c> type and pins the
/// stream-registration order so the journal stream IDs (8..14) line up with the registrations below.
/// To regenerate after an intentional wire-format change to the OrleansBinary codecs, re-run that
/// fixture in a worktree pinned to upstream/main, replace the fixture bytes, and accept the updated
/// Verify snapshot under <c>snapshots</c>.
/// </remarks>
[TestCategory("BVT")]
public sealed class UpstreamMainCompatibilityTests : JournalingTestBase
{
    [Fact]
    public async Task OrleansBinary_RecoversUpstreamMainWrittenJournal()
    {
        var journalBytes = LoadUpstreamMainJournal();
        var storage = new VolatileJournalStorage();
        await storage.AppendAsync(new ReadOnlySequence<byte>(journalBytes), default);

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

        await Verify(JournalSnapshotFormatting.FormatBinary(journalBytes), extension: "txt").UseDirectory("snapshots");
    }

    private static byte[] LoadUpstreamMainJournal([CallerFilePath] string sourceFile = "")
    {
        var fixturePath = Path.Combine(
            Path.GetDirectoryName(sourceFile)!,
            "fixtures",
            "UpstreamMainCompatibilityTests.upstream-main-journal.bin");

        return File.ReadAllBytes(fixturePath);
    }

    private DurableStates CreateStates(IJournalStorage storage)
    {
        var manager = CreateManager(storage);
        // Registration order MUST match the capture fixture so that journal stream IDs (8..14)
        // line up with what upstream/main wrote.
        return new DurableStates(
            manager,
            new DurableDictionary<string, int>("dict", manager,
                new OrleansBinaryDurableDictionaryCommandCodec<string, int>(ValueCodec<string>(), ValueCodec<int>(), SessionPool)),
            new DurableList<string>("list", manager,
                new OrleansBinaryDurableListCommandCodec<string>(ValueCodec<string>(), SessionPool)),
            new DurableQueue<int>("queue", manager,
                new OrleansBinaryDurableQueueCommandCodec<int>(ValueCodec<int>(), SessionPool)),
            new DurableSet<string>("set", manager,
                new OrleansBinaryDurableSetCommandCodec<string>(ValueCodec<string>(), SessionPool)),
            new DurableValue<int>("value", manager,
                new OrleansBinaryDurableValueCommandCodec<int>(ValueCodec<int>(), SessionPool)),
            new DurableState<string>("state", manager,
                new OrleansBinaryPersistentStateCommandCodec<string>(ValueCodec<string>(), SessionPool)),
            new DurableTaskCompletionSource<int>(
                "tcs",
                manager,
                new OrleansBinaryDurableTaskCompletionSourceCommandCodec<int>(ValueCodec<int>(), ValueCodec<Exception>(), SessionPool),
                Copier<int>(),
                Copier<Exception>()));
    }

    private JournaledStateManager CreateManager(IJournalStorage storage)
    {
        var shared = new JournaledStateManagerShared(
            LoggerFactory.CreateLogger<JournaledStateManager>(),
            Options.Create(ManagerOptions),
            TimeProvider.System,
            ServiceProvider);

        return new(shared, storage);
    }

    private IFieldCodec<T> ValueCodec<T>() => CodecProvider.GetCodec<T>();

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
