using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Providers;
using TestExtensions;
using UnitTests.Persistence;
using Orleans.Persistence.GoogleFirestore;

namespace Orleans.Tests.Google;

[TestCategory("Persistence"), TestCategory("GoogleFirestore"), TestCategory("GoogleCloud")]
[Collection(TestEnvironmentFixture.DefaultCollection)]
public class FirestoreStorageProviderTests : IClassFixture<TestEnvironmentFixture>, IAsyncLifetime
{
    private readonly IProviderRuntime _providerRuntime;
    private readonly ITestOutputHelper _output;
    private readonly Dictionary<string, string> _providerCfgProps = new();
    private GoogleFirestoreStorage _storage = default!;

    public FirestoreStorageProviderTests(ITestOutputHelper output, TestEnvironmentFixture fixture)
    {
        this._output = output;
        this._providerRuntime = new ClientProviderRuntime(
            fixture.InternalGrainFactory,
            fixture.Services,
            fixture.Services.GetRequiredService<ClientGrainContext>());
    }

    [SkippableTheory, TestCategory("Functional")]
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

        await Test_PersistenceProvider_WriteRead(testName, this._storage, grainState);
    }

    [SkippableTheory, TestCategory("Functional")]
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

        await Test_PersistenceProvider_WriteClearRead(testName, this._storage, grainState);
    }

    [SkippableTheory, TestCategory("Functional")]
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

        grainState = await Test_PersistenceProvider_WriteRead(testName, this._storage,
            grainState, grainId);

        await Test_PersistenceProvider_Read(testName, this._storage, grainState, grainId);
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
        this._output.WriteLine("{0} - Read time = {1}", store.GetType().FullName, readTime);

        var storedState = storedGrainState.State;
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
        this._output.WriteLine("{0} - Write time = {1} Read time = {2}", store.GetType().FullName, writeTime, readTime);
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
        this._output.WriteLine("{0} - Write time = {1} Read time = {2}", store.GetType().FullName, writeTime, readTime);
        Assert.NotNull(storedGrainState.State);
        Assert.Equal(default, storedGrainState.State.A);
        Assert.Equal(default, storedGrainState.State.B);
        Assert.Equal(default, storedGrainState.State.C);

        return storedGrainState;
    }

    public async Task InitializeAsync()
    {
        await GoogleEmulatorHost.Instance.EnsureStarted();

        var id = $"orleans-test-{Guid.NewGuid():N}";

        var options = new FirestoreStateStorageOptions
        {
            DeleteStateOnClear = true,
            EmulatorHost = GoogleEmulatorHost.FirestoreEndpoint,
            ProjectId = id,
        };

        var store = ActivatorUtilities.CreateInstance<GoogleFirestoreStorage>(this._providerRuntime.ServiceProvider, "StorageProviderTests", options);
        ISiloLifecycleSubject lifecycle = ActivatorUtilities.CreateInstance<SiloLifecycleSubject>(this._providerRuntime.ServiceProvider, NullLogger<SiloLifecycleSubject>.Instance);
        store.Participate(lifecycle);
        await lifecycle.OnStart();
        this._storage = store;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
