using System.Buffers;
using System.Buffers.Binary;
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
/// Recovers pinned legacy and released OrleansBinary journals, then verifies current append and
/// snapshot writes recover the same durable state.
/// </summary>
/// <remarks>
/// The binary fixture under <c>fixtures</c> was produced by the
/// <c>CaptureCrossVersionRecoveryFixture.EmitMainJournalHex</c> test running against
/// upstream/main (<c>5989958561</c>) using the legacy <c>StateMachineManager</c> and inlined writers.
/// The V1 hexadecimal fixtures were emitted by the codecs in the released
/// <c>Microsoft.Orleans.Journaling 10.3.1-alpha.1</c> package, built from
/// <c>137d9acc17830f15b13a4eb0058d6cee633cad5e</c>. Both journal fixtures exercise all seven durable
/// types and pin the registration order to stream IDs 8..14. Keep these inputs pinned to their
/// producing versions and add fixtures for future formats alongside them.
/// </remarks>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class UpstreamMainCompatibilityTests : JournalingTestBase, IDisposable
{
    [Fact]
    public async Task OrleansBinary_RecoversUpstreamMainWrittenJournal()
    {
        var journalBytes = LoadUpstreamMainJournal();
        var storage = new VolatileJournalStorage();
        await storage.AppendAsync(new ReadOnlySequence<byte>(journalBytes), TestContext.Current.CancellationToken);

        var states = CreateStates(storage);
        await using var manager = states.Manager;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await manager.InitializeAsync(cts.Token);

        await AssertRecoveredStates(states, appended: false);
        await Verify(JournalSnapshotFormatting.FormatBinary(journalBytes), extension: "txt").UseDirectory("snapshots");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrleansBinary_RecoversReleasedBaselineWrittenJournal(bool hasFormatMetadata)
    {
        var journalBytes = LoadReleasedFixture("journal");
        Assert.Equal(19, AssertV1Framing(journalBytes));
        var storage = await CreateStorage(journalBytes, hasFormatMetadata);
        var states = CreateStates(new CompatibilityStorage(storage));
        await using var manager = states.Manager;

        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        await AssertRecoveredStates(states, appended: false);
        Assert.Equal(journalBytes, Assert.Single(storage.Segments));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OrleansBinary_AppendsAndCompactsPersistedJournalWithRecoverableV1(
        bool legacyFraming,
        bool hasFormatMetadata)
    {
        var journalBytes = legacyFraming ? LoadUpstreamMainJournal() : LoadReleasedFixture("journal");
        var storage = await CreateStorage(journalBytes, hasFormatMetadata);
        var streamingStorage = new CompatibilityStorage(storage);
        var states = CreateStates(streamingStorage);
        await using (var manager = states.Manager)
        {
            await manager.InitializeAsync(TestContext.Current.CancellationToken);
            await AssertRecoveredStates(states, appended: false);

            states.Dictionary["alpha"] = 42;
            states.List.Add("fourth");
            states.Queue.Enqueue(30);
            states.Set.Add("baz");
            states.Value.Value = 43;
            ((IStorage<string>)states.State).State = "goodbye";
            await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        }

        Assert.Collection(storage.Segments,
            original => Assert.Equal(journalBytes, original),
            appended => Assert.Equal(LoadReleasedFixture("append"), appended));
        Assert.Equal(OrleansBinaryJournalFormat.JournalFormatKey, storage.StoredJournalFormatKey);

        var recovered = CreateStates(streamingStorage);
        await using (var manager = recovered.Manager)
        {
            await manager.InitializeAsync(TestContext.Current.CancellationToken);
            await AssertRecoveredStates(recovered, appended: true);

            streamingStorage.IsCompactionRequested = true;
            await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(9, AssertV1Framing(Assert.Single(storage.Segments)));
        Assert.Equal(OrleansBinaryJournalFormat.JournalFormatKey, storage.StoredJournalFormatKey);
        var compacted = CreateStates(streamingStorage);
        await using (var manager = compacted.Manager)
        {
            await manager.InitializeAsync(TestContext.Current.CancellationToken);
            await AssertRecoveredStates(compacted, appended: true);
        }
    }

    private static async Task AssertRecoveredStates(DurableStates states, bool appended)
    {
        Assert.Equal(2, states.Dictionary.Count);
        Assert.Equal(appended ? 42 : 1, states.Dictionary["alpha"]);
        Assert.Equal(2, states.Dictionary["beta"]);

        Assert.Equal(appended ? ["first", "second", "third", "fourth"] : ["first", "second", "third"], states.List);

        Assert.Equal(appended ? 3 : 2, states.Queue.Count);
        Assert.Equal(appended ? [10, 20, 30] : [10, 20], states.Queue);

        Assert.Equal(appended ? 3 : 2, states.Set.Count);
        Assert.True(states.Set.SetEquals(appended ? ["foo", "bar", "baz"] : ["foo", "bar"]));

        Assert.Equal(appended ? 43 : 42, states.Value.Value);

        Assert.Equal(appended ? "goodbye" : "hello", ((IStorage<string>)states.State).State);

        Assert.Equal(DurableTaskCompletionSourceStatus.Completed, states.Tcs.State.Status);
        Assert.Equal(99, states.Tcs.State.Value);
        Assert.Equal(99, await states.Tcs.Task.WaitAsync(TestContext.Current.CancellationToken));
    }

    private static int AssertV1Framing(byte[] bytes)
    {
        var entryCount = 0;
        var offset = 0;
        while (offset < bytes.Length)
        {
            Assert.True(bytes.Length - offset >= 9);
            Assert.Equal(1, bytes[offset]);
            var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 1, sizeof(uint)));
            Assert.True(bodyLength >= sizeof(uint));
            Assert.True(bodyLength <= bytes.Length - offset - 5);
            offset += checked(5 + (int)bodyLength);
            entryCount++;
        }

        Assert.Equal(bytes.Length, offset);
        return entryCount;
    }

    private static async Task<VolatileJournalStorage> CreateStorage(byte[] bytes, bool hasFormatMetadata)
    {
        var storage = new VolatileJournalStorage(hasFormatMetadata ? OrleansBinaryJournalFormat.JournalFormatKey : null);
        await storage.AppendAsync(new ReadOnlySequence<byte>(bytes), TestContext.Current.CancellationToken);
        Assert.Equal(hasFormatMetadata ? OrleansBinaryJournalFormat.JournalFormatKey : null, storage.StoredJournalFormatKey);
        storage.SetConfiguredJournalFormatKey(OrleansBinaryJournalFormat.JournalFormatKey);
        return storage;
    }

    private static byte[] LoadReleasedFixture(string kind, [CallerFilePath] string sourceFile = "")
    {
        var fixturePath = Path.Combine(
            Path.GetDirectoryName(sourceFile)!,
            "fixtures",
            $"UpstreamMainCompatibilityTests.released-10.3.1-v1-{kind}.hex");
        return Convert.FromHexString(File.ReadAllText(fixturePath).Trim());
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
            new JournaledPersistentState<string>("state", manager,
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

    public void Dispose() => ServiceProvider.Dispose();

    private sealed class CompatibilityStorage(VolatileJournalStorage storage) : IJournalStorage
    {
        public bool IsCompactionRequested { get; set; }

        public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            var metadata = await storage.GetMetadataAsync(cancellationToken);
            consumer.Read(ReadBytes(), metadata, complete: true);

            IEnumerable<ReadOnlyMemory<byte>> ReadBytes()
            {
                foreach (var segment in storage.Segments)
                {
                    for (var i = 0; i < segment.Length; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return segment.AsMemory(i, 1);
                    }
                }
            }
        }

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) =>
            storage.AppendAsync(value, cancellationToken);

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) =>
            storage.ReplaceAsync(value, cancellationToken);

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => storage.DeleteAsync(cancellationToken);
    }

    private sealed record DurableStates(
        JournaledStateManager Manager,
        DurableDictionary<string, int> Dictionary,
        DurableList<string> List,
        DurableQueue<int> Queue,
        DurableSet<string> Set,
        DurableValue<int> Value,
        JournaledPersistentState<string> State,
        DurableTaskCompletionSource<int> Tcs);
}
