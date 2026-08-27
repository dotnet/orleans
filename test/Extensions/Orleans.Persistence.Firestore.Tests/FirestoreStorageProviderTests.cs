using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.Providers;
using TestExtensions;
using UnitTests.Persistence;
using Orleans.Persistence.TestKit;
using Orleans.Persistence.Firestore;
using UnitTests.StorageTests.Relational;
using Xunit;

namespace Orleans.Persistence.Firestore.Tests;

[TestSuite("Functional")]
[TestProvider("GoogleCloud")]
[TestArea("Persistence")]
[TestCategory("Persistence"), TestCategory("Firestore"), TestCategory("GoogleCloud"), TestCategory("Functional")]
[Collection(TestEnvironmentFixture.DefaultCollection)]
public class FirestoreStorageProviderTests : IClassFixture<TestEnvironmentFixture>, IAsyncLifetime
{
    private readonly IProviderRuntime _providerRuntime;
    private readonly ITestOutputHelper _output;
    private readonly List<ISiloLifecycleSubject> _lifecycles = [];
    private string _rootCollectionName = default!;

    public FirestoreStorageProviderTests(ITestOutputHelper output, TestEnvironmentFixture fixture)
    {
        this._output = output;
        this._providerRuntime = new ClientProviderRuntime(
            fixture.InternalGrainFactory,
            fixture.Services,
            fixture.Services.GetRequiredService<ClientGrainContext>());
    }

    [TestSuite("Functional")]
    [Theory, TestCategory("Functional")]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData(400_000, false)]
    [InlineData(400_000, true)]
    public async Task WriteRead(int? stringLength, bool useJson)
    {
        var testName = string.Format("{0}({1} = {2}, {3} = {4})",
            nameof(WriteRead),
            nameof(stringLength), stringLength == null ? "default" : stringLength.ToString(),
            nameof(useJson), useJson);

        var grainState = TestStoreGrainState.NewRandomState(stringLength);
        var storage = await CreateStorage(useJson);

        await Test_PersistenceProvider_WriteRead(testName, storage, grainState);
    }

    [TestSuite("Functional")]
    [Theory, TestCategory("Functional")]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData(400_000, false)]
    [InlineData(400_000, true)]
    public async Task WriteClearRead(int? stringLength, bool useJson)
    {
        var testName = string.Format("{0}({1} = {2}, {3} = {4})",
            nameof(WriteClearRead),
            nameof(stringLength), stringLength == null ? "default" : stringLength.ToString(),
            nameof(useJson), useJson);

        var grainState = TestStoreGrainState.NewRandomState(stringLength);
        var storage = await CreateStorage(useJson);

        await Test_PersistenceProvider_WriteClearRead(testName, storage, grainState);
    }

    [Fact]
    public async Task StateNamesUseIndependentRecords()
    {
        var storage = await CreateStorage();
        var grainId = GrainId.Create("test", Guid.NewGuid().ToString("N"));
        var first = TestStoreGrainState.NewRandomState();
        var second = TestStoreGrainState.NewRandomState();

        await storage.WriteStateAsync("first", grainId, first);
        await storage.WriteStateAsync("second", grainId, second);

        var firstRead = new GrainState<TestStoreGrainState>(new());
        var secondRead = new GrainState<TestStoreGrainState>(new());
        await storage.ReadStateAsync("first", grainId, firstRead);
        await storage.ReadStateAsync("second", grainId, secondRead);

        var firstState = Assert.IsType<TestStoreGrainState>(first.State);
        var secondState = Assert.IsType<TestStoreGrainState>(second.State);
        var firstReadState = Assert.IsType<TestStoreGrainState>(firstRead.State);
        var secondReadState = Assert.IsType<TestStoreGrainState>(secondRead.State);
        Assert.Equal(firstState.A, firstReadState.A);
        Assert.Equal(firstState.B, firstReadState.B);
        Assert.Equal(firstState.C, firstReadState.C);
        Assert.Equal(secondState.A, secondReadState.A);
        Assert.Equal(secondState.B, secondReadState.B);
        Assert.Equal(secondState.C, secondReadState.C);
    }

    [Fact]
    public async Task MissingReadResetsState()
    {
        var storage = await CreateStorage();
        var grainState = TestStoreGrainState.NewRandomState();
        grainState.ETag = "stale";
        grainState.RecordExists = true;

        await storage.ReadStateAsync(
            "missing",
            GrainId.Create("test", Guid.NewGuid().ToString("N")),
            grainState);

        Assert.False(grainState.RecordExists);
        Assert.Null(grainState.ETag);
        var state = Assert.IsType<TestStoreGrainState>(grainState.State);
        Assert.Equal(default, state.A);
        Assert.Equal(default, state.B);
        Assert.Equal(default, state.C);
    }

    [Fact]
    public async Task ClearBeforeWriteSucceeds()
    {
        var storage = await CreateStorage();
        var grainState = TestStoreGrainState.NewRandomState();

        await storage.ClearStateAsync(
            "never-written",
            GrainId.Create("test", Guid.NewGuid().ToString("N")),
            grainState);

        Assert.False(grainState.RecordExists);
        Assert.Null(grainState.ETag);
        var state = Assert.IsType<TestStoreGrainState>(grainState.State);
        Assert.Equal(default, state.A);
        Assert.Equal(default, state.B);
        Assert.Equal(default, state.C);
    }

    [Fact]
    public async Task ClearedDocumentCanBeRewritten()
    {
        var storage = await CreateStorage(deleteStateOnClear: false);
        var grainId = GrainId.Create("test", Guid.NewGuid().ToString("N"));
        var grainState = TestStoreGrainState.NewRandomState();

        await storage.WriteStateAsync("state", grainId, grainState);
        await storage.ClearStateAsync("state", grainId, grainState);

        var clearedState = new GrainState<TestStoreGrainState>(new());
        await storage.ReadStateAsync("state", grainId, clearedState);
        Assert.False(clearedState.RecordExists);
        Assert.NotNull(clearedState.ETag);

        clearedState.State = TestStoreGrainState.NewRandomState().State;
        await storage.WriteStateAsync("state", grainId, clearedState);

        var rewrittenState = new GrainState<TestStoreGrainState>(new());
        await storage.ReadStateAsync("state", grainId, rewrittenState);
        Assert.True(rewrittenState.RecordExists);
        var expected = Assert.IsType<TestStoreGrainState>(clearedState.State);
        var actual = Assert.IsType<TestStoreGrainState>(rewrittenState.State);
        Assert.Equal(expected.A, actual.A);
        Assert.Equal(expected.B, actual.B);
        Assert.Equal(expected.C, actual.C);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SerializedNullIsReadAsMissingState(bool useJson)
    {
        var storage = await CreateStorage(useJson);
        var grainId = GrainId.Create("test", Guid.NewGuid().ToString("N"));
        var writtenState = new GrainState<TestStoreGrainState>(null);

        await storage.WriteStateAsync("state", grainId, writtenState);

        Assert.True(writtenState.RecordExists);
        Assert.NotNull(writtenState.ETag);

        var readState = TestStoreGrainState.NewRandomState();
        await storage.ReadStateAsync("state", grainId, readState);

        Assert.False(readState.RecordExists);
        Assert.Equal(writtenState.ETag, readState.ETag);
        var state = Assert.IsType<TestStoreGrainState>(readState.State);
        Assert.Equal(default, state.A);
        Assert.Equal(default, state.B);
        Assert.Equal(default, state.C);
    }

    [Fact]
    public async Task WildcardETagOverwritesExistingState()
    {
        var storage = await CreateStorage();
        var grainId = GrainId.Create("test", Guid.NewGuid().ToString("N"));
        var initialState = TestStoreGrainState.NewRandomState();
        await storage.WriteStateAsync("state", grainId, initialState);

        var replacementState = TestStoreGrainState.NewRandomState();
        replacementState.ETag = "*";
        await storage.WriteStateAsync("state", grainId, replacementState);

        Assert.True(replacementState.RecordExists);
        Assert.NotNull(replacementState.ETag);
        Assert.NotEqual("*", replacementState.ETag);

        var readState = new GrainState<TestStoreGrainState>(new());
        await storage.ReadStateAsync("state", grainId, readState);
        Assert.Equal(replacementState.ETag, readState.ETag);
        var expected = Assert.IsType<TestStoreGrainState>(replacementState.State);
        var actual = Assert.IsType<TestStoreGrainState>(readState.State);
        Assert.Equal(expected.A, actual.A);
        Assert.Equal(expected.B, actual.B);
        Assert.Equal(expected.C, actual.C);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WildcardETagClearsExistingState(bool deleteStateOnClear)
    {
        var storage = await CreateStorage(deleteStateOnClear: deleteStateOnClear);
        var grainId = GrainId.Create("test", Guid.NewGuid().ToString("N"));
        var grainState = TestStoreGrainState.NewRandomState();
        await storage.WriteStateAsync("state", grainId, grainState);

        grainState.ETag = "*";
        await storage.ClearStateAsync("state", grainId, grainState);

        Assert.False(grainState.RecordExists);
        Assert.Equal(deleteStateOnClear, grainState.ETag is null);

        var readState = TestStoreGrainState.NewRandomState();
        await storage.ReadStateAsync("state", grainId, readState);
        Assert.False(readState.RecordExists);
    }

    [Fact]
    public async Task DuplicateWriteThrowsInconsistentStateException()
    {
        var tests = new CommonStorageTests(await CreateStorage());
        var exception = await tests.PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException();

        Assert.Null(exception.CurrentEtag);
        Assert.Equal("Unknown", exception.StoredEtag);
    }

    [Fact]
    public async Task WriteWithUnknownETagThrowsInconsistentStateException()
    {
        var tests = new CommonStorageTests(await CreateStorage());
        var exception = await tests.PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException();

        Assert.NotNull(exception.CurrentEtag);
        Assert.Equal("Unknown", exception.StoredEtag);
    }

    [Fact]
    public async Task ClearWithStaleETagThrowsInconsistentStateException()
    {
        var storage = await CreateStorage();
        var grainId = GrainId.Create("test", Guid.NewGuid().ToString("N"));
        var grainState = TestStoreGrainState.NewRandomState();
        await storage.WriteStateAsync("state", grainId, grainState);
        var staleETag = grainState.ETag;

        grainState.State = TestStoreGrainState.NewRandomState().State;
        await storage.WriteStateAsync("state", grainId, grainState);

        grainState.ETag = staleETag;
        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.ClearStateAsync("state", grainId, grainState));

        Assert.Equal(staleETag, exception.CurrentEtag);
        Assert.Equal("Unknown", exception.StoredEtag);
    }

    [Fact, TestCategory("Functional"), TestCategory("ModelBased")]
    public async Task FirestoreStorage_ModelBasedGeneratedConformance()
    {
        var storage = await CreateStorage(deleteStateOnClear: true);
        var runner = new GrainStorageModelBasedTestRunner(storage, "Firestore", _output.WriteLine);

        await runner.RunGeneratedConformanceTests(TestContext.Current.CancellationToken);
    }

    [Fact, TestCategory("Functional"), TestCategory("ModelBased")]
    public async Task FirestoreStorage_ClearWritesTombstone_ModelBasedGeneratedConformance()
    {
        var storage = await CreateStorage(deleteStateOnClear: false);
        var runner = new GrainStorageModelBasedTestRunner(storage, "FirestoreClearWritesTombstone", _output.WriteLine);
        await runner.RunGeneratedConformanceTests(TestContext.Current.CancellationToken);
    }

    [TestSuite("Functional")]
    [Theory, TestCategory("Functional")]
    [InlineData(null, true, false)]
    [InlineData(null, false, true)]
    [InlineData(400_000, true, false)]
    [InlineData(400_000, false, true)]
    public async Task ChangeReadFormat(int? stringLength, bool useJsonForWrite, bool useJsonForRead)
    {
        var testName = string.Format("{0}({1} = {2}, {3} = {4}, {5} = {6})",
            nameof(ChangeReadFormat),
            nameof(stringLength), stringLength == null ? "default" : stringLength.ToString(),
            nameof(useJsonForWrite), useJsonForWrite,
            nameof(useJsonForRead), useJsonForRead);

        var grainState = TestStoreGrainState.NewRandomState(stringLength);
        var grainId = GrainId.Create("test", Guid.NewGuid().ToString("N"));
        var writeStorage = await CreateStorage(useJsonForWrite, useFallback: true);

        grainState = await Test_PersistenceProvider_WriteRead(testName, writeStorage,
            grainState, grainId);

        var readStorage = await CreateStorage(useJsonForRead, useFallback: true);
        await Test_PersistenceProvider_Read(testName, readStorage, grainState, grainId);
    }

    private async Task Test_PersistenceProvider_Read(string grainTypeName, IGrainStorage store,
        GrainState<TestStoreGrainState>? grainState = null, GrainId grainId = default)
    {
        var reference = grainId.IsDefault ? GrainId.Create("test", Guid.NewGuid().ToString("N")) : grainId;

        grainState ??= new GrainState<TestStoreGrainState>(new TestStoreGrainState());
        var storedGrainState = new GrainState<TestStoreGrainState>(new TestStoreGrainState());

        var sw = new Stopwatch();
        sw.Start();

        await store.ReadStateAsync(grainTypeName, reference, storedGrainState);

        var readTime = sw.Elapsed;
        this._output.WriteLine("{0} - Read time = {1}", store.GetType().FullName!, readTime);

        var storedState = storedGrainState.State;
        Assert.NotNull(grainState.State);
        Assert.NotNull(storedState);
        Assert.Equal(grainState.State.A, storedState.A);
        Assert.Equal(grainState.State.B, storedState.B);
        Assert.Equal(grainState.State.C, storedState.C);
    }

    private async Task<GrainState<TestStoreGrainState>> Test_PersistenceProvider_WriteRead(string grainTypeName,
        IGrainStorage store, GrainState<TestStoreGrainState>? grainState = null, GrainId grainId = default)
    {
        var reference = grainId.IsDefault ? GrainId.Create("test", Guid.NewGuid().ToString("N")) : grainId;

        grainState ??= TestStoreGrainState.NewRandomState();

        var sw = new Stopwatch();
        sw.Start();

        await store.WriteStateAsync(grainTypeName, reference, grainState);

        var writeTime = sw.Elapsed;
        sw.Restart();

        var storedGrainState = new GrainState<TestStoreGrainState>
        {
            State = new TestStoreGrainState()
        };
        await store.ReadStateAsync(grainTypeName, reference, storedGrainState);
        var readTime = sw.Elapsed;
        this._output.WriteLine("{0} - Write time = {1} Read time = {2}", store.GetType().FullName!, writeTime, readTime);
        Assert.NotNull(grainState.State);
        Assert.NotNull(storedGrainState.State);
        Assert.Equal(grainState.State.A, storedGrainState.State.A);
        Assert.Equal(grainState.State.B, storedGrainState.State.B);
        Assert.Equal(grainState.State.C, storedGrainState.State.C);

        return storedGrainState;
    }

    private async Task<GrainState<TestStoreGrainState>> Test_PersistenceProvider_WriteClearRead(string grainTypeName,
        IGrainStorage store, GrainState<TestStoreGrainState>? grainState = null, GrainId grainId = default)
    {
        var reference = grainId.IsDefault ? GrainId.Create("test", Guid.NewGuid().ToString("N")) : grainId;

        grainState ??= TestStoreGrainState.NewRandomState();

        var sw = new Stopwatch();
        sw.Start();

        await store.WriteStateAsync(grainTypeName, reference, grainState);

        var writeTime = sw.Elapsed;
        sw.Restart();

        await store.ClearStateAsync(grainTypeName, reference, grainState);

        var storedGrainState = new GrainState<TestStoreGrainState>
        {
            State = new TestStoreGrainState()
        };
        await store.ReadStateAsync(grainTypeName, reference, storedGrainState);
        var readTime = sw.Elapsed;
        this._output.WriteLine("{0} - Write time = {1} Read time = {2}", store.GetType().FullName!, writeTime, readTime);
        Assert.NotNull(storedGrainState.State);
        Assert.Equal(default, storedGrainState.State.A);
        Assert.Equal(default, storedGrainState.State.B);
        Assert.Equal(default, storedGrainState.State.C);

        return storedGrainState;
    }

    private async Task<FirestoreGrainStorage> CreateStorage(
        bool useJson = false,
        bool useFallback = false,
        bool deleteStateOnClear = true)
    {
        var options = new FirestoreStateStorageOptions
        {
            DeleteStateOnClear = deleteStateOnClear,
            EmulatorHost = GoogleEmulatorHost.FirestoreEndpoint,
            ProjectId = GoogleEmulatorHost.ProjectId,
            RootCollectionName = _rootCollectionName,
        };

        var binarySerializer = new OrleansGrainStorageSerializer(
            this._providerRuntime.ServiceProvider.GetRequiredService<Serializer>());
        var jsonOptions = this._providerRuntime.ServiceProvider
            .GetRequiredService<IOptions<OrleansJsonSerializerOptions>>();
        var jsonSerializer = new JsonGrainStorageSerializer(new OrleansJsonSerializer(jsonOptions));
        options.GrainStorageSerializer = useFallback
            ? useJson
                ? new GrainStorageSerializer(jsonSerializer, binarySerializer)
                : new GrainStorageSerializer(binarySerializer, jsonSerializer)
            : useJson ? jsonSerializer : binarySerializer;

        var store = ActivatorUtilities.CreateInstance<FirestoreGrainStorage>(this._providerRuntime.ServiceProvider, "StorageProviderTests", options);
        ISiloLifecycleSubject lifecycle = ActivatorUtilities.CreateInstance<SiloLifecycleSubject>(this._providerRuntime.ServiceProvider, NullLogger<SiloLifecycleSubject>.Instance);
        store.Participate(lifecycle);
        await lifecycle.OnStart();
        _lifecycles.Add(lifecycle);
        return store;
    }

    public ValueTask InitializeAsync()
    {
        _rootCollectionName = $"orleans-test-{Guid.NewGuid():N}";
        _ = GoogleEmulatorHost.FirestoreEndpoint;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lifecycle in Enumerable.Reverse(_lifecycles))
        {
            await lifecycle.OnStop();
        }
    }
}
