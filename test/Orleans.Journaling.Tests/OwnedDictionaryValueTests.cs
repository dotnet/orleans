using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.WireProtocol;
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
    public void AddAndSet_RetainBeforeEncoding_CallerReleasePreservesStoredValue(bool useIndexer)
    {
        using var fixture = CreateFixture();
        using var caller = new ResourceProbe(42);
        if (useIndexer) fixture.Dictionary["key"] = caller;
        else fixture.Dictionary.Add("key", caller);

        var stored = fixture.Dictionary["key"];
        Assert.NotSame(caller, stored);
        Assert.Same(stored, Assert.Single(fixture.ValueCodec.Encoded));
        Assert.Equal(2, caller.Owners);
        caller.Dispose();
        Assert.Equal(42, stored.Value);
        Assert.Equal(1, stored.Owners);
        fixture.Dictionary.Dispose();
        fixture.Dictionary.Dispose();
        Assert.Equal(1, stored.Disposes);
        Assert.Equal(0, stored.Owners);
        Assert.Equal(1, fixture.Lifecycle.Retains);
        Assert.Equal([stored], fixture.Lifecycle.Released);
    }

    [Fact]
    public void MultipleKeysAndSelfReplacement_AcquireAndRetireIndependentOwners()
    {
        using var fixture = CreateFixture();
        using var caller = new ResourceProbe(17);
        fixture.Dictionary.Add("first", caller);
        caller.Dispose();
        var first = fixture.Dictionary["first"];
        fixture.Dictionary.Add("neighbor", first);
        var neighbor = fixture.Dictionary["neighbor"];
        fixture.Dictionary["first"] = first;
        var replacement = fixture.Dictionary["first"];

        Assert.NotSame(first, neighbor);
        Assert.NotSame(first, replacement);
        Assert.Equal(1, first.Disposes);
        Assert.Equal(2, replacement.Owners);
        Assert.Equal(17, replacement.Value);
        Assert.Equal(17, neighbor.Value);
        Assert.True(fixture.Dictionary.Remove("first"));
        Assert.False(fixture.Dictionary.Remove("first"));
        Assert.Equal(1, replacement.Disposes);
        Assert.Equal(17, neighbor.Value);
        Assert.Equal(1, neighbor.Owners);
        fixture.Dictionary.Clear();
        fixture.Dictionary.Dispose();
        Assert.Equal(1, neighbor.Disposes);
        Assert.Equal(0, caller.Owners);
        Assert.Equal(3, fixture.Lifecycle.Retains);
        Assert.Equal([first, replacement, neighbor], fixture.Lifecycle.Released);
    }

    [Fact]
    public void DuplicateAddAndPairRemoval_ReleaseOnlyTheMatchingStoredOwner()
    {
        using var fixture = CreateFixture();
        using var caller = new ResourceProbe(23);
        using var other = new ResourceProbe(99);
        fixture.Dictionary.Add("key", caller);
        var stored = fixture.Dictionary["key"];
        var before = fixture.JournalBytes();

        Assert.Throws<ArgumentException>(() => fixture.Dictionary.Add("key", other));
        Assert.False(fixture.Dictionary.Remove("missing"));
        Assert.False(fixture.Dictionary.Remove(new KeyValuePair<string, ResourceProbe>("key", other)));
        Assert.False(fixture.Dictionary.Remove(new KeyValuePair<string, ResourceProbe>("missing", stored)));
        Assert.Equal(before, fixture.JournalBytes());
        Assert.Equal(1, fixture.Lifecycle.Retains);
        Assert.Empty(fixture.Lifecycle.Released);
        Assert.Equal(23, stored.Value);
        Assert.Equal(1, other.Owners);

        Assert.True(fixture.Dictionary.Remove(new KeyValuePair<string, ResourceProbe>("key", stored)));
        Assert.Empty(fixture.Dictionary);
        Assert.Equal([stored], fixture.Lifecycle.Released);
        Assert.Equal(1, stored.Disposes);
        Assert.Equal(1, caller.Owners);
        Assert.Equal(23, caller.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EncodingFailure_ReleasesAcquiredOwner_PreservesJournalAndEntries(bool useIndexer)
    {
        using var fixture = CreateFixture();
        using var old = new ResourceProbe(10);
        using var neighbor = new ResourceProbe(20);
        using var proposed = new ResourceProbe(30);
        fixture.Dictionary.Add("old", old);
        fixture.Dictionary.Add("neighbor", neighbor);
        var stored = fixture.Dictionary["old"];
        var storedNeighbor = fixture.Dictionary["neighbor"];
        var before = fixture.JournalBytes();
        var error = new InvalidOperationException("encoding failure");
        fixture.ValueCodec.Failure = error;

        Assert.Same(error, Record.Exception(() =>
        {
            if (useIndexer) fixture.Dictionary["old"] = proposed;
            else fixture.Dictionary.Add("new", proposed);
        }));

        var rejected = fixture.ValueCodec.Encoded[^1];
        Assert.NotSame(proposed, rejected);
        Assert.Equal([rejected], fixture.Lifecycle.Released);
        Assert.Equal(1, rejected.Disposes);
        Assert.Equal(1, proposed.Owners);
        Assert.Equal(30, proposed.Value);
        Assert.Same(stored, fixture.Dictionary["old"]);
        Assert.Same(storedNeighbor, fixture.Dictionary["neighbor"]);
        Assert.Equal(10, stored.Value);
        Assert.Equal(20, storedNeighbor.Value);
        Assert.Equal(2, fixture.Dictionary.Count);
        Assert.False(fixture.Dictionary.ContainsKey("new"));
        Assert.Equal(before, fixture.JournalBytes());
    }

    [Fact]
    public void RetainFailure_PreservesCallerAndJournal()
    {
        using var fixture = CreateFixture();
        using var caller = new ResourceProbe(31);
        var error = new InvalidOperationException("retain failure");
        fixture.Lifecycle.Failure = error;

        Assert.Same(error, Record.Exception(() => fixture.Dictionary.Add("key", caller)));

        Assert.Equal(1, fixture.Lifecycle.Retains);
        Assert.Empty(fixture.ValueCodec.Encoded);
        Assert.Empty(fixture.Lifecycle.Released);
        Assert.Empty(fixture.JournalBytes());
        Assert.Empty(fixture.Dictionary);
        Assert.Equal(1, caller.Owners);
        Assert.Equal(31, caller.Value);
    }

    [Fact]
    public void ReplayInsertionFailure_ReleasesTransferredOwner()
    {
        using var fixture = CreateFixture();
        var decoded = new ResourceProbe(11);
        var handler = (IDurableDictionaryCommandHandler<string, ResourceProbe>)fixture.Dictionary;

        Assert.Throws<ArgumentNullException>(() => handler.ApplySet(null!, decoded));

        Assert.Equal(1, decoded.Disposes);
        Assert.Equal(0, decoded.Owners);
        Assert.Equal([decoded], fixture.Lifecycle.Released);
        Assert.Equal(0, fixture.Lifecycle.Retains);
        Assert.Empty(fixture.Dictionary);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("pair-remove")]
    [InlineData("clear")]
    public void RemoveAndClearEncodingFailure_PreserveStoredOwners(string operation)
    {
        using var fixture = CreateFixture();
        using var caller = new ResourceProbe(84);
        fixture.Dictionary.Add("key", caller);
        var stored = fixture.Dictionary["key"];
        var before = fixture.JournalBytes();
        var error = new InvalidOperationException("command encoding failure");
        fixture.Codec.Failure = error;

        Assert.Same(error, Record.Exception(() =>
        {
            switch (operation)
            {
                case "remove": fixture.Dictionary.Remove("key"); break;
                case "pair-remove": fixture.Dictionary.Remove(new KeyValuePair<string, ResourceProbe>("key", stored)); break;
                case "clear": fixture.Dictionary.Clear(); break;
                default: throw new ArgumentException(operation);
            }
        }));

        Assert.Same(stored, fixture.Dictionary["key"]);
        Assert.Equal(84, stored.Value);
        Assert.Equal(2, caller.Owners);
        Assert.Empty(fixture.Lifecycle.Released);
        Assert.Equal(before, fixture.JournalBytes());
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("state-reset")]
    [InlineData("command-reset")]
    [InlineData("replay-clear")]
    [InlineData("dispose")]
    public void ClearResetAndDispose_RetireEveryStoredOwnerOnce(string operation)
    {
        using var fixture = CreateFixture();
        using var first = new ResourceProbe(1);
        using var second = new ResourceProbe(2);
        fixture.Dictionary.Add("first", first);
        fixture.Dictionary.Add("second", second);
        var owners = fixture.Dictionary.Values.ToArray();
        void Act()
        {
            switch (operation)
            {
                case "clear": fixture.Dictionary.Clear(); break;
                case "state-reset": ((IStateMachine)fixture.Dictionary).Reset(fixture.Writer); break;
                case "command-reset": ((IDurableDictionaryCommandHandler<string, ResourceProbe>)fixture.Dictionary).Reset(10); break;
                case "replay-clear": ((IDurableDictionaryCommandHandler<string, ResourceProbe>)fixture.Dictionary).ApplyClear(); break;
                case "dispose": fixture.Dictionary.Dispose(); break;
                default: throw new ArgumentException(operation);
            }
        }

        Act();
        Act();
        fixture.Dictionary.Dispose();
        Assert.Empty(fixture.Dictionary);
        Assert.Equal(owners, fixture.Lifecycle.Released);
        Assert.All(owners, owner => Assert.Equal(1, owner.Disposes));
        Assert.Equal(1, first.Owners);
        Assert.Equal(1, second.Owners);
        Assert.Equal(1, first.Value);
        Assert.Equal(2, second.Value);
    }

    [Fact]
    public async Task JournalReplay_TransfersDecodedOwners_ReplacementRemovalAndDeletionRetireOnce()
    {
        var token = TestContext.Current.CancellationToken;
        var storage = new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey);
        var source = CreateTestSystem(storage);
        await using var sourceManager = source.Manager;
        var sourceCodec = CreateValueCodec();
        var sourceLifecycle = new ProbeLifecycle();
        using var sourceDictionary = new DurableDictionary<string, ResourceProbe>(
            "owned", source.Manager, CreateCodec(sourceCodec), sourceLifecycle);
        await source.Manager.InitializeAsync(token);
        using var first = new ResourceProbe(11);
        using var second = new ResourceProbe(22);
        using var neighbor = new ResourceProbe(33);
        sourceDictionary.Add("key", first);
        sourceDictionary.Add("neighbor", neighbor);
        await source.Manager.WriteStateAsync(token);
        sourceDictionary["key"] = second;
        sourceDictionary.Add("removed", first);
        sourceDictionary.Remove("removed");
        await source.Manager.WriteStateAsync(token);
        Assert.NotEmpty(storage.Segments);

        var recovered = CreateTestSystem(storage);
        await using var recoveredManager = recovered.Manager;
        var replayCodec = CreateValueCodec();
        var replayLifecycle = new ProbeLifecycle();
        using var dictionary = new DurableDictionary<string, ResourceProbe>(
            "owned", recovered.Manager, CreateCodec(replayCodec), replayLifecycle);
        await recovered.Manager.InitializeAsync(token);

        Assert.Equal(0, replayLifecycle.Retains);
        Assert.Equal(4, replayCodec.Decoded.Count);
        Assert.Equal([replayCodec.Decoded[0], replayCodec.Decoded[3]], replayLifecycle.Released);
        Assert.Equal(22, dictionary["key"].Value);
        Assert.Equal(33, dictionary["neighbor"].Value);
        Assert.Same(replayCodec.Decoded[2], dictionary["key"]);
        Assert.Same(replayCodec.Decoded[1], dictionary["neighbor"]);
        Assert.Equal(1, dictionary["key"].Owners);
        Assert.Equal(1, dictionary["neighbor"].Owners);

        await recovered.Manager.DeleteStateAsync(token);
        Assert.Empty(dictionary);
        Assert.Empty(storage.Segments);
        dictionary.Dispose();
        Assert.Equal(4, replayLifecycle.Released.Count);
        Assert.All(replayCodec.Decoded, value =>
        {
            Assert.Equal(1, value.Disposes);
            Assert.Equal(0, value.Owners);
        });
        Assert.Equal(22, sourceDictionary["key"].Value);
        Assert.Equal(33, sourceDictionary["neighbor"].Value);
    }

    [Fact]
    public void SnapshotReplay_TransfersDecodedOwners_ResetsPreviousContents()
    {
        using var source = CreateFixture();
        using var caller = new ResourceProbe(71);
        source.Dictionary.Add("key", caller);
        var stored = source.Dictionary["key"];
        byte[]? previous = null;
        for (var i = 0; i < 3; i++)
        {
            var payload = CodecTestHelpers.WriteEntry(writer => ((IStateMachine)source.Dictionary).WriteSnapshot(writer));
            if (previous is not null) Assert.Equal(previous, payload);
            previous = payload;
            using var target = CreateFixture();
            using var prior = new ResourceProbe(99);
            target.Dictionary.Add("prior", prior);
            var oldOwner = target.Dictionary["prior"];
            target.Codec.Apply(CodecTestHelpers.ReadBuffer(payload), target.Dictionary);
            var decoded = Assert.Single(target.ValueCodec.Decoded);

            Assert.Same(decoded, target.Dictionary["key"]);
            Assert.Equal(71, decoded.Value);
            Assert.Equal(["key"], target.Dictionary.Keys);
            Assert.Equal([oldOwner], target.Lifecycle.Released);
            Assert.Equal(1, target.Lifecycle.Retains);
            Assert.Equal(1, decoded.Owners);
            target.Dictionary.Dispose();
            Assert.Equal(1, decoded.Disposes);
            Assert.Equal(0, decoded.Owners);
            Assert.Equal(2, caller.Owners);
            Assert.Equal(71, stored.Value);
            Assert.Empty(source.Lifecycle.Released);
            Assert.Equal(1, source.Lifecycle.Retains);
        }
    }

    [Fact]
    public void ScopedResolution_UsesRegisteredLifecycle_AndDisposesStoredOwners()
    {
        using var journal = new OrleansBinaryJournalBufferWriter();
        using var caller = new ResourceProbe(56);
        var lifecycle = new ProbeLifecycle();
        var codec = CreateCodec(CreateValueCodec());
        var builder = new TestSiloBuilder();
        builder.Services.AddLogging();
        builder.Services.AddSerializer();
        builder.Services.AddKeyedSingleton<TimeProvider>(JournalingTimeProviderNames.Journaling, TimeProvider.System);
        builder.Services.AddSingleton<IJournaledStateManager>(CreateLocalManager(journal));
        builder.Services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey);
        builder.AddJournaling();
        builder.Services.AddSingleton<IDurableDictionaryValueLifecycle<ResourceProbe>>(lifecycle);
        builder.Services.AddKeyedSingleton<IDurableDictionaryCommandCodec<string, ResourceProbe>>(OrleansBinaryJournalFormat.JournalFormatKey, codec);
        using var provider = builder.Services.BuildServiceProvider();
        ResourceProbe stored;
        using (var scope = provider.CreateScope())
        {
            var dictionary = scope.ServiceProvider.GetRequiredKeyedService<IDurableDictionary<string, ResourceProbe>>("owned");
            Assert.Same(dictionary, scope.ServiceProvider.GetRequiredKeyedService<IDurableDictionary<string, ResourceProbe>>("owned"));
            dictionary.Add("key", caller);
            stored = dictionary["key"];
            Assert.Equal(56, stored.Value);
            Assert.Equal(2, caller.Owners);
            Assert.Equal(1, lifecycle.Retains);
        }

        Assert.Equal([stored], lifecycle.Released);
        Assert.Equal(1, stored.Disposes);
        Assert.Equal(1, caller.Owners);
        Assert.Equal(56, caller.Value);
    }

    [Fact]
    public async Task StandaloneManagerDisposal_LeavesComponentOwnershipWithCaller()
    {
        var system = CreateTestSystem();
        await using var manager = system.Manager;
        var lifecycle = new ProbeLifecycle();
        using var dictionary = new DurableDictionary<string, ResourceProbe>(
            "owned", manager, CreateCodec(CreateValueCodec()), lifecycle);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        using var caller = new ResourceProbe(59);
        dictionary.Add("key", caller);
        var stored = dictionary["key"];

        await manager.DisposeAsync();

        Assert.Same(stored, dictionary["key"]);
        Assert.Equal(59, stored.Value);
        Assert.Empty(lifecycle.Released);
        Assert.Equal(2, caller.Owners);
        dictionary.Dispose();
        Assert.Equal([stored], lifecycle.Released);
        Assert.Equal(1, stored.Disposes);
        Assert.Equal(1, caller.Owners);
        Assert.Equal(59, caller.Value);
    }

    [Fact]
    public void UnregisteredValues_PreserveReferenceAndDisposalBehavior()
    {
        using var fixture = CreateFixture(registerLifecycle: false);
        using var first = new ResourceProbe(17);
        using var second = new ResourceProbe(23);
        fixture.Dictionary.Add("key", first);
        Assert.Same(first, fixture.Dictionary["key"]);
        fixture.Dictionary["key"] = second;
        Assert.Same(second, fixture.Dictionary["key"]);
        Assert.True(fixture.Dictionary.Remove("key"));
        fixture.Dictionary.Add("key", first);
        fixture.Dictionary.Clear();
        fixture.Dictionary.Add("key", second);
        ((IStateMachine)fixture.Dictionary).Reset(fixture.Writer);
        fixture.Dictionary.Add("survivor", first);
        fixture.Dictionary.Dispose();
        Assert.Same(first, fixture.Dictionary["survivor"]);
        fixture.Dictionary["survivor"] = second;
        Assert.Same(second, fixture.Dictionary["survivor"]);
        Assert.Equal(0, first.Disposes);
        Assert.Equal(0, second.Disposes);
        Assert.Equal(1, first.Owners);
        Assert.Equal(1, second.Owners);
    }

    [Fact]
    public void DisposedDictionary_RejectsMutationsBeforeAcquiringOwners()
    {
        using var fixture = CreateFixture();
        using var caller = new ResourceProbe(26);
        fixture.Dictionary.Dispose();
        Assert.Throws<ObjectDisposedException>(() => fixture.Dictionary.Add("key", caller));
        Assert.Throws<ObjectDisposedException>(() => fixture.Dictionary["key"] = caller);
        Assert.Throws<ObjectDisposedException>(fixture.Dictionary.Clear);
        Assert.Throws<ObjectDisposedException>(() => ((IStateMachine)fixture.Dictionary).Reset(fixture.Writer));
        Assert.Equal(0, fixture.Lifecycle.Retains);
        Assert.Empty(fixture.Lifecycle.Released);
        Assert.Empty(fixture.ValueCodec.Encoded);
        Assert.Empty(fixture.JournalBytes());
        Assert.Equal(1, caller.Owners);
        Assert.Equal(26, caller.Value);
    }

    private ProbeCodec CreateValueCodec() => new(CodecProvider.GetCodec<int>());

    private CommandCodec CreateCodec(ProbeCodec valueCodec) =>
        new(new OrleansBinaryDurableDictionaryCommandCodec<string, ResourceProbe>(CodecProvider.GetCodec<string>(), valueCodec, SessionPool));

    private Fixture CreateFixture(bool registerLifecycle = true)
    {
        var valueCodec = CreateValueCodec();
        return new(valueCodec, CreateCodec(valueCodec), registerLifecycle);
    }

    private static IJournaledStateManager CreateLocalManager(OrleansBinaryJournalBufferWriter journal)
    {
        var manager = Substitute.For<IJournaledStateManager>();
        manager.When(value => value.RegisterStateMachine(Arg.Any<string>(), Arg.Any<IStateMachine>()))
            .Do(call => call.Arg<IStateMachine>().Reset(journal.CreateJournalStreamWriter(new(1))));
        return manager;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly OrleansBinaryJournalBufferWriter _journal = new();

        public Fixture(ProbeCodec valueCodec, CommandCodec codec, bool registerLifecycle)
        {
            ValueCodec = valueCodec;
            Codec = codec;
            Dictionary = new("owned", CreateLocalManager(_journal), codec, registerLifecycle ? Lifecycle : null);
        }

        public ProbeLifecycle Lifecycle { get; } = new();
        public ProbeCodec ValueCodec { get; }
        public CommandCodec Codec { get; }
        public DurableDictionary<string, ResourceProbe> Dictionary { get; }
        public JournalStreamWriter Writer => _journal.CreateJournalStreamWriter(new(1));

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

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class ResourceProbe : IDisposable
    {
        private readonly Resource _resource;

        public ResourceProbe(int value) : this(new Resource(value)) { }

        private ResourceProbe(Resource resource)
        {
            _resource = resource;
            _resource.Owners++;
        }

        public int Owners => _resource.Owners;
        public int Disposes { get; private set; }
        public int Value
        {
            get
            {
                ObjectDisposedException.ThrowIf(Disposes != 0, this);
                return _resource.Value;
            }
        }

        public ResourceProbe Retain()
        {
            _ = Value;
            return new(_resource);
        }

        public void Dispose()
        {
            if (Disposes != 0) return;
            Disposes++;
            _resource.Owners--;
        }

        private sealed class Resource(int value)
        {
            public int Value { get; } = value;
            public int Owners { get; set; }
        }
    }

    private sealed class ProbeLifecycle : IDurableDictionaryValueLifecycle<ResourceProbe>
    {
        public int Retains { get; private set; }
        public List<ResourceProbe> Released { get; } = [];
        public Exception? Failure { get; set; }

        public ResourceProbe Retain(ResourceProbe value)
        {
            Retains++;
            if (Failure is { } error) throw error;
            return value.Retain();
        }

        public void Release(ResourceProbe value)
        {
            Assert.DoesNotContain(value, Released);
            Assert.Equal(0, value.Disposes);
            Released.Add(value);
            value.Dispose();
        }
    }

    private sealed class ProbeCodec(IFieldCodec<int> inner) : IFieldCodec<ResourceProbe>
    {
        public List<ResourceProbe> Encoded { get; } = [];
        public List<ResourceProbe> Decoded { get; } = [];
        public Exception? Failure { get; set; }

        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, [AllowNull] Type expectedType, [AllowNull] ResourceProbe value)
            where TBufferWriter : IBufferWriter<byte>
        {
            Encoded.Add(value!);
            inner.WriteField(ref writer, fieldIdDelta, typeof(int), value!.Value);
            if (Failure is { } error) throw error;
        }

        public ResourceProbe ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            var value = new ResourceProbe(inner.ReadValue(ref reader, field));
            Decoded.Add(value);
            return value;
        }
    }

    private sealed class CommandCodec(IDurableDictionaryCommandCodec<string, ResourceProbe> inner) : IDurableDictionaryCommandCodec<string, ResourceProbe>
    {
        public Exception? Failure { get; set; }

        public void WriteSet(string key, ResourceProbe value, JournalStreamWriter writer) => inner.WriteSet(key, value, writer);
        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<string, ResourceProbe>> items, JournalStreamWriter writer) => inner.WriteSnapshot(items, writer);
        public void Apply(JournalBufferReader input, IDurableDictionaryCommandHandler<string, ResourceProbe> consumer) => inner.Apply(input, consumer);

        public void WriteRemove(string key, JournalStreamWriter writer)
        {
            ThrowEncodingFailure(writer);
            inner.WriteRemove(key, writer);
        }

        public void WriteClear(JournalStreamWriter writer)
        {
            ThrowEncodingFailure(writer);
            inner.WriteClear(writer);
        }

        private void ThrowEncodingFailure(JournalStreamWriter writer)
        {
            if (Failure is not { } error) return;
            using var entry = writer.BeginEntry();
            entry.Writer.Write<byte>([0xff]);
            throw error;
        }
    }
}
