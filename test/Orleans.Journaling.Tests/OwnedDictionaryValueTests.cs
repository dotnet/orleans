using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class OwnedDictionaryValueTests : JournalingTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddAndSet_RetainBeforeEncoding_CallerDisposeLeavesStoredBytesAlive(bool useIndexer)
    {
        using var resource = new Resource([3, 1, 4, 1, 5]);
        using var fixture = new Fixture(ServiceProvider);
        var caller = resource.Take();
        if (useIndexer) fixture.Dictionary["owned"] = caller;
        else fixture.Dictionary.Add("owned", caller);

        Assert.Equal(1, fixture.Lifecycle.Retains);
        Assert.Equal(0, fixture.Lifecycle.Releases);
        Assert.Equal(3, Assert.Single(fixture.Codec.SetReferenceCounts));
        Assert.Equal(new byte[] { 3, 1, 4, 1, 5 }, Assert.Single(fixture.Codec.SetBytes));
        caller.Dispose();
        Assert.Equal(2, References(resource.Page));
        Assert.Equal(new byte[] { 3, 1, 4, 1, 5 }, fixture.Dictionary["owned"].ToArray());
        Assert.Equal(["owned"], fixture.Dictionary.Keys);
        fixture.Dictionary.Dispose();
        Assert.Equal(1, fixture.Lifecycle.Releases);
        Assert.Equal(1, References(resource.Page)); // Only the independent writer anchor remains.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EncodingFailure_ReleasesRetainedCopy_PreservesOriginalErrorAndNeighbor(bool useIndexer)
    {
        using var old = new Resource([10, 11]);
        using var neighbor = new Resource([20, 21]);
        using var proposed = new Resource([30, 31, 32]);
        using var fixture = new Fixture(ServiceProvider);
        using var oldCaller = old.Take();
        using var neighborCaller = neighbor.Take();
        using var proposedCaller = proposed.Take();
        fixture.Dictionary.Add("old", oldCaller);
        fixture.Dictionary.Add("neighbor", neighborCaller);
        var before = fixture.JournalBytes();
        var expected = new InvalidOperationException("original source encoding failure");
        fixture.Codec.SetFailure = expected;

        var actual = Record.Exception(() =>
        {
            if (useIndexer) fixture.Dictionary["old"] = proposedCaller;
            else fixture.Dictionary.Add("new", proposedCaller);
        });

        Assert.Same(expected, actual);
        Assert.Equal(3, fixture.Lifecycle.Retains);
        Assert.Equal(1, fixture.Lifecycle.Releases);
        Assert.Equal(3, fixture.Codec.SetReferenceCounts[^1]);
        Assert.Equal(2, References(proposed.Page));
        Assert.Equal(3, References(old.Page));
        Assert.Equal(3, References(neighbor.Page));
        Assert.Equal(new byte[] { 10, 11 }, fixture.Dictionary["old"].ToArray());
        Assert.Equal(new byte[] { 20, 21 }, fixture.Dictionary["neighbor"].ToArray());
        Assert.Equal(new byte[] { 30, 31, 32 }, proposedCaller.ToArray());
        Assert.Equal(2, fixture.Dictionary.Count);
        Assert.False(fixture.Dictionary.ContainsKey("new"));
        Assert.Equal(before, fixture.JournalBytes());
    }

    [Fact]
    public void RetainFailure_DoesNotEncodeMutateOrReleaseCaller()
    {
        using var resource = new Resource([6, 2, 6]);
        using var fixture = new Fixture(ServiceProvider);
        using var caller = resource.Take();
        var expected = new InvalidOperationException("retain failure");
        fixture.Lifecycle.RetainFailure = expected;

        Assert.Same(expected, Record.Exception(() => fixture.Dictionary.Add("key", caller)));

        Assert.Equal(1, fixture.Lifecycle.Retains);
        Assert.Equal(0, fixture.Lifecycle.Releases);
        Assert.Empty(fixture.Codec.SetBytes);
        Assert.Empty(fixture.Dictionary);
        Assert.Empty(fixture.JournalBytes());
        Assert.Equal(2, References(resource.Page));
        Assert.Equal(new byte[] { 6, 2, 6 }, caller.ToArray());
    }

    [Fact]
    public void ReplayApplySet_ConsumesOwnedValue_ReplacementAndRemoveReleaseOnce()
    {
        using var first = new Resource([1, 2, 3]);
        using var second = new Resource([4, 5, 6]);
        using var neighbor = new Resource([7, 8, 9]);
        using var fixture = new Fixture(ServiceProvider);
        var handler = (IDurableDictionaryCommandHandler<string, ArcBuffer>)fixture.Dictionary;
        handler.ApplySet("key", first.Take());
        handler.ApplySet("neighbor", neighbor.Take());
        Assert.Equal(0, fixture.Lifecycle.Retains);
        Assert.Equal(2, References(first.Page));
        handler.ApplySet("key", second.Take());
        Assert.Equal(0, fixture.Lifecycle.Retains);
        Assert.Equal(1, fixture.Lifecycle.Releases);
        Assert.Equal(1, References(first.Page));
        Assert.Equal(2, References(second.Page));
        Assert.Equal(new byte[] { 4, 5, 6 }, fixture.Dictionary["key"].ToArray());
        Assert.Equal(new byte[] { 7, 8, 9 }, fixture.Dictionary["neighbor"].ToArray());

        handler.ApplyRemove("key");
        handler.ApplyRemove("missing");
        Assert.Equal(2, fixture.Lifecycle.Releases);
        Assert.Equal(1, References(second.Page));
        Assert.Equal(["neighbor"], fixture.Dictionary.Keys);
        Assert.Empty(fixture.JournalBytes());
        fixture.Dictionary.Dispose();
        Assert.Equal(3, fixture.Lifecycle.Releases);
        Assert.Equal(1, References(neighbor.Page));
    }

    [Fact]
    public void ReplacementAndRemove_ReleaseOnlyStoredOwnership_KeepNeighborAlive()
    {
        using var first = new Resource([11]);
        using var second = new Resource([22]);
        using var neighbor = new Resource([33]);
        using var fixture = new Fixture(ServiceProvider);
        using var firstCaller = first.Take();
        using var secondCaller = second.Take();
        using var neighborCaller = neighbor.Take();
        fixture.Dictionary.Add("key", firstCaller);
        fixture.Dictionary.Add("neighbor", neighborCaller);
        fixture.Dictionary["key"] = secondCaller;
        Assert.Equal(3, fixture.Lifecycle.Retains);
        Assert.Equal(1, fixture.Lifecycle.Releases);
        Assert.Equal(2, References(first.Page));
        Assert.Equal(3, References(second.Page));
        Assert.Equal(new byte[] { 22 }, fixture.Dictionary["key"].ToArray());
        Assert.True(fixture.Dictionary.Remove("key"));
        Assert.False(fixture.Dictionary.Remove("key"));
        Assert.Equal(2, fixture.Lifecycle.Releases);
        Assert.Equal(2, References(second.Page));
        Assert.Equal(3, References(neighbor.Page));
        Assert.Equal(new byte[] { 33 }, fixture.Dictionary["neighbor"].ToArray());
        Assert.Equal(new byte[] { 11 }, firstCaller.ToArray());
        Assert.Equal(["neighbor"], fixture.Dictionary.Keys);
    }

    [Fact]
    public void MultipleKeysAndSelfReplacement_KeepIndependentPinsOnEveryPage()
    {
        var expected = Enumerable.Range(0, ArcBufferWriter.MinimumPageSize + 5)
            .Select(index => (byte)(index * 17 + 3)).ToArray();
        using var resource = new Resource(expected);
        var pages = new[] { resource.Page, resource.Page.Next! };
        Assert.NotNull(pages[1]);
        Assert.Null(pages[1].Next);
        using var fixture = new Fixture(ServiceProvider);
        var caller = resource.Take();
        fixture.Dictionary.Add("first", caller);
        caller.Dispose();
        Assert.All(pages, page => Assert.Equal(2, References(page)));

        fixture.Dictionary.Add("neighbor", fixture.Dictionary["first"]);
        Assert.All(pages, page => Assert.Equal(3, References(page)));
        fixture.Dictionary["first"] = fixture.Dictionary["first"];
        Assert.Equal(4, fixture.Codec.SetReferenceCounts[^1]); // Retain precedes encoding and old-owner release.
        Assert.Equal(3, fixture.Lifecycle.Retains);
        Assert.Equal(1, fixture.Lifecycle.Releases);
        Assert.All(pages, page => Assert.Equal(3, References(page)));
        Assert.Equal(expected, fixture.Dictionary["first"].ToArray());
        Assert.Equal(expected, fixture.Dictionary["neighbor"].ToArray());

        Assert.True(fixture.Dictionary.Remove("first"));
        Assert.All(pages, page => Assert.Equal(2, References(page)));
        Assert.Equal(expected, fixture.Dictionary["neighbor"].ToArray());
        Assert.Equal(["neighbor"], fixture.Dictionary.Keys);
        fixture.Dictionary.Clear();
        fixture.Dictionary.Dispose();
        Assert.Equal(3, fixture.Lifecycle.Releases);
        Assert.All(pages, page => Assert.Equal(1, References(page)));
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("state-reset")]
    [InlineData("command-reset")]
    [InlineData("replay-clear")]
    [InlineData("dispose")]
    public void ClearResetAndDispose_ReleaseEveryValueExactlyOnce(string operation)
    {
        using var first = new Resource([1, 3]);
        using var second = new Resource([2, 4]);
        using var fixture = new Fixture(ServiceProvider);
        using var firstCaller = first.Take();
        using var secondCaller = second.Take();
        fixture.Dictionary.Add("first", firstCaller);
        fixture.Dictionary.Add("second", secondCaller);
        void Act()
        {
            switch (operation)
            {
                case "clear": fixture.Dictionary.Clear(); break;
                case "state-reset": ((IStateMachine)fixture.Dictionary).Reset(fixture.Writer); break;
                case "command-reset": ((IDurableDictionaryCommandHandler<string, ArcBuffer>)fixture.Dictionary).Reset(10); break;
                case "replay-clear": ((IDurableDictionaryCommandHandler<string, ArcBuffer>)fixture.Dictionary).ApplyClear(); break;
                case "dispose": fixture.Dictionary.Dispose(); break;
                default: throw new ArgumentException(operation);
            }
        }

        Act();
        Act();
        fixture.Dictionary.Dispose();
        Assert.Empty(fixture.Dictionary);
        Assert.Equal(2, fixture.Lifecycle.Retains);
        Assert.Equal(2, fixture.Lifecycle.Releases);
        Assert.Equal(2, References(first.Page));
        Assert.Equal(2, References(second.Page));
        Assert.Equal(new byte[] { 1, 3 }, firstCaller.ToArray());
        Assert.Equal(new byte[] { 2, 4 }, secondCaller.ToArray());
    }

    [Fact]
    public void PairRemove_OnlyMatchingPairReleasesOwnership()
    {
        using var resource = new Resource([8, 6, 7]);
        using var other = new Resource([5, 3, 0, 9]);
        using var fixture = new Fixture(ServiceProvider);
        using var caller = resource.Take();
        using var otherCaller = other.Take();
        fixture.Dictionary.Add("key", caller);
        var before = fixture.JournalBytes();
        Assert.False(fixture.Dictionary.Remove(new KeyValuePair<string, ArcBuffer>("key", otherCaller)));
        Assert.False(fixture.Dictionary.Remove(new KeyValuePair<string, ArcBuffer>("missing", caller)));
        Assert.Equal(before, fixture.JournalBytes());
        Assert.Equal(0, fixture.Lifecycle.Releases);
        Assert.Equal(3, References(resource.Page));
        Assert.Equal(new byte[] { 8, 6, 7 }, fixture.Dictionary["key"].ToArray());

        Assert.True(fixture.Dictionary.Remove(new KeyValuePair<string, ArcBuffer>("key", fixture.Dictionary["key"])));
        Assert.Empty(fixture.Dictionary);
        Assert.Equal(1, fixture.Lifecycle.Releases);
        Assert.Equal(2, References(resource.Page));
        Assert.Equal(2, References(other.Page));
    }

    [Fact]
    public void DuplicateAddAndMissingRemove_DoNotRetainOrRelease()
    {
        using var resource = new Resource([9, 2]);
        using var fixture = new Fixture(ServiceProvider);
        using var caller = resource.Take();
        fixture.Dictionary.Add("key", caller);
        var before = fixture.JournalBytes();
        Assert.Throws<ArgumentException>(() => fixture.Dictionary.Add("key", caller));
        Assert.False(fixture.Dictionary.Remove("missing"));
        Assert.Equal(1, fixture.Lifecycle.Retains);
        Assert.Equal(0, fixture.Lifecycle.Releases);
        Assert.Equal(3, References(resource.Page));
        Assert.Equal(before, fixture.JournalBytes());
        Assert.Equal(new byte[] { 9, 2 }, fixture.Dictionary["key"].ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoveAndClearEncodingFailure_PreserveStoredAndCallerOwnership(bool clear)
    {
        using var resource = new Resource([1, 9, 8, 4]);
        using var fixture = new Fixture(ServiceProvider);
        using var caller = resource.Take();
        fixture.Dictionary.Add("key", caller);
        var before = fixture.JournalBytes();
        var expected = new InvalidOperationException("remove/clear encoding error");
        fixture.Codec.OtherFailure = expected;
        Assert.Same(expected, Record.Exception(() =>
        {
            if (clear) fixture.Dictionary.Clear();
            else fixture.Dictionary.Remove("key");
        }));
        Assert.Equal(0, fixture.Lifecycle.Releases);
        Assert.Equal(3, References(resource.Page));
        Assert.Equal(new byte[] { 1, 9, 8, 4 }, fixture.Dictionary["key"].ToArray());
        Assert.Equal(before, fixture.JournalBytes());
    }

    [Fact]
    public void MissingLifecycle_PreservesOrdinaryDisposableValueBehavior()
    {
        using var journal = new OrleansBinaryJournalBufferWriter();
        var manager = new LocalManager(journal);
        using var dictionary = new DurableDictionary<string, DisposableValue>("ordinary", manager, new OrdinaryCodec());
        var first = new DisposableValue(17);
        var second = new DisposableValue(23);
        dictionary.Add("key", first);
        Assert.Same(first, dictionary["key"]);
        dictionary["key"] = second;
        Assert.Same(second, dictionary["key"]);
        Assert.True(dictionary.Remove("key"));
        ((IDurableDictionaryCommandHandler<string, DisposableValue>)dictionary).ApplySet("key", first);
        ((IStateMachine)dictionary).Reset(journal.CreateJournalStreamWriter(new(1)));
        dictionary.Add("key", second);
        dictionary.Clear();
        dictionary.Add("survivor", first);
        dictionary.Dispose();
        Assert.Equal(0, first.Disposes);
        Assert.Equal(0, second.Disposes);
        Assert.Equal(17, first.Value);
        Assert.Equal(23, second.Value);
        Assert.Same(first, dictionary["survivor"]);
        dictionary["survivor"] = second;
        Assert.Same(second, dictionary["survivor"]);
        dictionary.Clear();
        Assert.Empty(dictionary);
    }

    [Fact]
    public void DisposedResourceDictionary_RejectsWritesBeforeRetainingOrEncoding()
    {
        using var resource = new Resource([2, 0, 2, 6]);
        using var caller = resource.Take();
        using var fixture = new Fixture(ServiceProvider);
        fixture.Dictionary.Dispose();
        Assert.Throws<ObjectDisposedException>(() => fixture.Dictionary.Add("key", caller));
        Assert.Throws<ObjectDisposedException>(() => fixture.Dictionary["key"] = caller);
        Assert.Throws<ObjectDisposedException>(fixture.Dictionary.Clear);
        Assert.Throws<ObjectDisposedException>(() => ((IStateMachine)fixture.Dictionary).Reset(fixture.Writer));
        Assert.Equal(0, fixture.Lifecycle.Retains);
        Assert.Equal(0, fixture.Lifecycle.Releases);
        Assert.Empty(fixture.Codec.SetBytes);
        Assert.Empty(fixture.JournalBytes());
        Assert.Empty(fixture.Dictionary);
        Assert.Equal(2, References(resource.Page));
        Assert.Equal(new byte[] { 2, 0, 2, 6 }, caller.ToArray());
    }

    [Fact]
    public void RepeatedSerializationAndSnapshots_DoNotConsumeStoredOwnership()
    {
        using var resource = new Resource([0, 255, 128, 42, 7]);
        using var neighbor = new Resource([16, 32]);
        using var fixture = new Fixture(ServiceProvider);
        var caller = resource.Take();
        var neighborCaller = neighbor.Take();
        fixture.Dictionary.Add("key", caller);
        fixture.Dictionary.Add("neighbor", neighborCaller);
        caller.Dispose();
        neighborCaller.Dispose();
        var serializer = ServiceProvider.GetRequiredService<Serializer<ArcBuffer>>();
        byte[]? previousSnapshot = null;
        for (var i = 0; i < 3; i++)
        {
            var encoded = serializer.SerializeToArray(fixture.Dictionary["key"]);
            using var decoded = serializer.Deserialize(encoded);
            Assert.Equal(new byte[] { 0, 255, 128, 42, 7 }, decoded.ToArray());
            using var snapshot = new OrleansBinaryJournalBufferWriter();
            ((IStateMachine)fixture.Dictionary).WriteSnapshot(snapshot.CreateJournalStreamWriter(new(1)));
            using var snapshotBytes = snapshot.GetBuffer();
            var bytes = snapshotBytes.ToArray();
            Assert.NotEmpty(bytes);
            if (previousSnapshot is not null) Assert.Equal(previousSnapshot, bytes);
            previousSnapshot = bytes;
            Assert.Equal(2, References(resource.Page));
            Assert.Equal(2, References(neighbor.Page));
            Assert.Equal(new byte[] { 0, 255, 128, 42, 7 }, fixture.Dictionary["key"].ToArray());
            Assert.Equal(new byte[] { 16, 32 }, fixture.Dictionary["neighbor"].ToArray());
            Assert.Equal(2, fixture.Lifecycle.Retains);
            Assert.Equal(0, fixture.Lifecycle.Releases);
        }
    }

    [Fact]
    public void ScopedResolution_DisposesDictionaryButNotCallerOwnership()
    {
        Assert.True(typeof(IDurableDictionaryValueLifecycle<ArcBuffer>).IsPublic);
        using var resource = new Resource([12, 34, 56]);
        using var caller = resource.Take();
        using var journal = new OrleansBinaryJournalBufferWriter();
        var lifecycle = new ArcLifecycle();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSerializer();
        ConfigureBinaryJournalingServices(services);
        services.AddSingleton<IDurableDictionaryValueLifecycle<ArcBuffer>>(lifecycle);
        services.AddSingleton<IJournaledStateManager>(new LocalManager(journal));
        services.AddSingleton(sp => new JournaledStateManagerShared(
            sp.GetRequiredService<ILogger<JournaledStateManager>>(), Options.Create(ManagerOptions), TimeProvider.System, sp));
        services.AddKeyedScoped<IDurableDictionary<string, ArcBuffer>>("owned", (sp, key) =>
            new DurableDictionary<string, ArcBuffer>((string)key!, sp.GetRequiredService<IJournaledStateManager>(),
                sp.GetRequiredService<JournaledStateManagerShared>(), sp));
        using var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var dictionary = scope.ServiceProvider.GetRequiredKeyedService<IDurableDictionary<string, ArcBuffer>>("owned");
            dictionary.Add("key", caller);
            Assert.Equal(1, lifecycle.Retains);
            Assert.Equal(3, References(resource.Page));
            Assert.Equal(new byte[] { 12, 34, 56 }, dictionary["key"].ToArray());
        }

        Assert.Equal(1, lifecycle.Releases);
        Assert.Equal(2, References(resource.Page));
        Assert.Equal(new byte[] { 12, 34, 56 }, caller.ToArray());
    }

    [Fact]
    public async Task StandaloneManagerDispose_LeavesDictionaryLifetimeWithCaller()
    {
        using var resource = new Resource([31, 41, 59]);
        using var caller = resource.Take();
        var lifecycle = new ArcLifecycle();
        var manager = CreateTestSystem().Manager;
        using var dictionary = new DurableDictionary<string, ArcBuffer>("owned", manager, BinaryCodec(), lifecycle);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        dictionary.Add("key", caller);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await manager.DisposeAsync();

        Assert.Equal(0, lifecycle.Releases);
        Assert.Equal(3, References(resource.Page));
        Assert.Equal(new byte[] { 31, 41, 59 }, dictionary["key"].ToArray());
        dictionary.Dispose();
        dictionary.Dispose();
        Assert.Equal(1, lifecycle.Releases);
        Assert.Equal(2, References(resource.Page));
        Assert.Equal(new byte[] { 31, 41, 59 }, caller.ToArray());
    }

    [Fact]
    public async Task DeleteState_ResetsAndReleasesOwnedDictionaryValues()
    {
        using var resource = new Resource([2, 7, 1, 8]);
        using var neighbor = new Resource([28, 18]);
        using var caller = resource.Take();
        using var neighborCaller = neighbor.Take();
        var lifecycle = new ArcLifecycle();
        await using var manager = CreateTestSystem().Manager;
        using var dictionary = new DurableDictionary<string, ArcBuffer>("owned", manager, BinaryCodec(), lifecycle);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        dictionary.Add("key", caller);
        dictionary.Add("neighbor", neighborCaller);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await manager.DeleteStateAsync(TestContext.Current.CancellationToken);

        Assert.Empty(dictionary);
        Assert.Equal(2, lifecycle.Releases);
        Assert.Equal(2, References(resource.Page));
        Assert.Equal(2, References(neighbor.Page));
        Assert.Equal(new byte[] { 2, 7, 1, 8 }, caller.ToArray());
        Assert.Equal(new byte[] { 28, 18 }, neighborCaller.ToArray());
        dictionary.Dispose();
        Assert.Equal(2, lifecycle.Releases);
    }

    [Fact]
    public async Task RecoveryFailure_ResetReleasesAlreadyDecodedValueAndPreservesError()
    {
        using var resource = new Resource([9, 8, 7, 6]);
        using var caller = resource.Take();
        var storage = new VolatileJournalStorage();
        await using (var sourceManager = CreateTestSystem(storage).Manager)
        {
            using var source = new DurableDictionary<string, ArcBuffer>("owned", sourceManager, BinaryCodec(), new ArcLifecycle());
            await sourceManager.InitializeAsync(TestContext.Current.CancellationToken);
            source.Add("key", caller);
            await sourceManager.WriteStateAsync(TestContext.Current.CancellationToken);
        }

        var expected = new InvalidOperationException("failure after owned decode");
        var codec = new ObservingCodec(BinaryCodec()) { ReplayFailure = expected };
        var lifecycle = new ArcLifecycle();
        await using var manager = CreateTestSystem(storage).Manager;
        using var dictionary = new DurableDictionary<string, ArcBuffer>("owned", manager, codec, lifecycle);
        var actual = await Record.ExceptionAsync(() => manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.NotNull(actual);
        Assert.Same(expected, actual!.GetBaseException());
        Assert.Equal(0, lifecycle.Retains);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, dictionary["key"].ToArray());
        var decoded = dictionary["key"];
        var decodedPage = decoded.First;
        Assert.Equal(1, References(decodedPage));

        // Standalone states remain caller-owned even when recovery fails. Reset releases the
        // consumed decoded value; the failed manager does not secretly dispose caller state.
        using var journal = new OrleansBinaryJournalBufferWriter();
        ((IStateMachine)dictionary).Reset(journal.CreateJournalStreamWriter(new(1)));
        Assert.Empty(dictionary);
        Assert.Equal(1, lifecycle.Releases);
        Assert.Equal(0, References(decodedPage));
        Assert.Equal(2, References(resource.Page));
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, caller.ToArray());
        dictionary.Dispose();
        Assert.Equal(1, lifecycle.Releases);
    }

    private IDurableDictionaryCommandCodec<string, ArcBuffer> BinaryCodec() =>
        ServiceProvider.GetRequiredKeyedService<IDurableDictionaryCommandCodec<string, ArcBuffer>>(OrleansBinaryJournalFormat.JournalFormatKey);

    // Read-only observation of the real page count: no access to private mutation methods,
    // no replacement runtime abstraction, and no global pool configuration.
    private static int References(ArcBufferPage page) => (int)typeof(ArcBufferPage)
        .GetProperty("ReferenceCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

    private sealed class Resource : IDisposable
    {
        private readonly ArcBufferWriter _writer = new();
        public ArcBufferPage Page { get; }
        public Resource(byte[] bytes)
        {
            _writer.Write(bytes);
            var borrowed = _writer.PeekSlice(bytes.Length);
            Page = borrowed.First;
            borrowed.Dispose();
            Assert.Equal(1, References(Page));
        }
        public ArcBuffer Take() => _writer.PeekSlice(_writer.Length);
        public void Dispose() => _writer.Dispose();
    }

    private sealed class ArcLifecycle : IDurableDictionaryValueLifecycle<ArcBuffer>
    {
        public int Retains { get; private set; }
        public int Releases { get; private set; }
        public Exception? RetainFailure { get; set; }
        public ArcBuffer Retain(ArcBuffer value)
        {
            Retains++;
            if (RetainFailure is { } error) throw error;
            return value.Slice(0, value.Length);
        }
        public void Release(ArcBuffer value)
        {
            Releases++;
            value.Dispose();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly OrleansBinaryJournalBufferWriter _journal = new();
        public ArcLifecycle Lifecycle { get; } = new();
        public ObservingCodec Codec { get; }
        public DurableDictionary<string, ArcBuffer> Dictionary { get; }
        public JournalStreamWriter Writer => _journal.CreateJournalStreamWriter(new(1));
        public Fixture(IServiceProvider services)
        {
            Codec = new(services.GetRequiredKeyedService<IDurableDictionaryCommandCodec<string, ArcBuffer>>(OrleansBinaryJournalFormat.JournalFormatKey));
            Dictionary = new("owned", new LocalManager(_journal), Codec, Lifecycle);
        }
        public byte[] JournalBytes()
        {
            using var buffer = _journal.GetBuffer();
            return buffer.ToArray();
        }
        public void Dispose()
        {
            Dictionary.Dispose();
            _journal.Dispose();
        }
    }

    private sealed class LocalManager(OrleansBinaryJournalBufferWriter writer) : IJournaledStateManager
    {
        public IList<IJournaledStateHook> Hooks { get; } = [];
        public void RegisterStateMachine(string name, IStateMachine state) => state.Reset(writer.CreateJournalStreamWriter(new(1)));
        public bool TryGetStateMachine(string name, [NotNullWhen(true)] out IStateMachine? stateMachine)
        {
            stateMachine = null;
            return false;
        }
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;
        public ValueTask WriteStateAsync(CancellationToken cancellationToken) => default;
        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;
        public ValueTask DisposeAsync() => default;
    }

    private sealed class ObservingCodec(IDurableDictionaryCommandCodec<string, ArcBuffer> inner) : IDurableDictionaryCommandCodec<string, ArcBuffer>
    {
        public List<int> SetReferenceCounts { get; } = [];
        public List<byte[]> SetBytes { get; } = [];
        public Exception? SetFailure { get; set; }
        public Exception? OtherFailure { get; set; }
        public Exception? ReplayFailure { get; set; }
        public void WriteSet(string key, ArcBuffer value, JournalStreamWriter writer)
        {
            SetReferenceCounts.Add(References(value.First));
            SetBytes.Add(value.ToArray());
            ThrowAfterPartialWrite(writer, SetFailure);
            inner.WriteSet(key, value, writer);
        }
        public void WriteRemove(string key, JournalStreamWriter writer)
        {
            ThrowAfterPartialWrite(writer, OtherFailure);
            inner.WriteRemove(key, writer);
        }
        public void WriteClear(JournalStreamWriter writer)
        {
            ThrowAfterPartialWrite(writer, OtherFailure);
            inner.WriteClear(writer);
        }
        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<string, ArcBuffer>> items, JournalStreamWriter writer) => inner.WriteSnapshot(items, writer);
        public void Apply(JournalBufferReader input, IDurableDictionaryCommandHandler<string, ArcBuffer> consumer)
        {
            inner.Apply(input, consumer);
            if (ReplayFailure is { } error) throw error;
        }
        private static void ThrowAfterPartialWrite(JournalStreamWriter writer, Exception? error)
        {
            if (error is null) return;
            using var entry = writer.BeginEntry();
            entry.Writer.GetSpan(1)[0] = 0xEE;
            entry.Writer.Advance(1);
            throw error;
        }
    }

    private sealed class DisposableValue(int value) : IDisposable
    {
        public int Value { get; } = value;
        public int Disposes { get; private set; }
        public void Dispose() => Disposes++;
    }

    private sealed class OrdinaryCodec : IDurableDictionaryCommandCodec<string, DisposableValue>
    {
        public void WriteSet(string key, DisposableValue value, JournalStreamWriter writer) => Commit(writer);
        public void WriteRemove(string key, JournalStreamWriter writer) => Commit(writer);
        public void WriteClear(JournalStreamWriter writer) => Commit(writer);
        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<string, DisposableValue>> items, JournalStreamWriter writer) => Commit(writer);
        public void Apply(JournalBufferReader input, IDurableDictionaryCommandHandler<string, DisposableValue> consumer) => throw new NotSupportedException();
        private static void Commit(JournalStreamWriter writer)
        {
            using var entry = writer.BeginEntry();
            entry.Writer.GetSpan(1)[0] = 1;
            entry.Writer.Advance(1);
            entry.Commit();
        }
    }
}
