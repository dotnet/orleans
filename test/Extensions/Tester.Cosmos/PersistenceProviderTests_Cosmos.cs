using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using TestExtensions;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Providers;
using Orleans.Configuration;
using Orleans.Persistence.Cosmos;
using UnitTests.Persistence;

namespace Tester.Cosmos.Persistence;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("Persistence"), TestCategory("Cosmos")]
public class PersistenceProviderTests_Cosmos
{
    private readonly IProviderRuntime providerRuntime;
    private readonly Dictionary<string, string> providerCfgProps = new Dictionary<string, string>();
    private readonly ITestOutputHelper output;
    private readonly TestEnvironmentFixture fixture;
    private readonly string _clusterId;
    private readonly string _serviceId;

    public PersistenceProviderTests_Cosmos(ITestOutputHelper output, TestEnvironmentFixture fixture)
    {
        CosmosTestUtils.CheckCosmosStorage();

        this.output = output;
        this.fixture = fixture;
        providerRuntime = new ClientProviderRuntime(
            fixture.InternalGrainFactory,
            fixture.Services,
            fixture.Services.GetRequiredService<ClientGrainContext>());
        providerCfgProps.Clear();
        _clusterId = Guid.NewGuid().ToString("N");
        _serviceId = Guid.NewGuid().ToString("N");
    }

    private async Task<CosmosGrainStorage> InitializeStorage()
    {
        var options = new CosmosGrainStorageOptions();

        options.ConfigureTestDefaults();

        var pkProvider = new DefaultPartitionKeyProvider();
        var clusterOptions = new ClusterOptions { ClusterId = _clusterId, ServiceId = _serviceId };

        var store = ActivatorUtilities.CreateInstance<CosmosGrainStorage>(providerRuntime.ServiceProvider, options, clusterOptions, "TestStorage", pkProvider);
        var lifecycle = ActivatorUtilities.CreateInstance<SiloLifecycleSubject>(providerRuntime.ServiceProvider);
        store.Participate(lifecycle);
        await lifecycle.OnStart();
        return store;
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task PersistenceProvider_Azure_Read()
    {
        const string testName = nameof(PersistenceProvider_Azure_Read);

        var store = await InitializeStorage();
        await Test_PersistenceProvider_Read(testName, store, null, grainId: GrainId.Create("testgrain", Guid.NewGuid().ToString()));
    }

    [SkippableTheory, TestCategory("Functional")]
    [InlineData(null)]
    [InlineData(15 * 64 * 1024 - 256)]
    [InlineData(15 * 32 * 1024 - 256)]
    public async Task PersistenceProvider_Azure_WriteRead(int? stringLength)
    {
        var testName = string.Format("{0}({1} = {2})",
            nameof(PersistenceProvider_Azure_WriteRead),
            nameof(stringLength), stringLength == null ? "default" : stringLength.ToString());

        var grainState = TestStoreGrainState.NewRandomState(stringLength);

        var store = await InitializeStorage();

        await Test_PersistenceProvider_WriteRead(testName, store, grainState, GrainId.Create("testgrain", Guid.NewGuid().ToString()));
    }

    [SkippableTheory, TestCategory("Functional")]
    [InlineData(null)]
    [InlineData(15 * 64 * 1024 - 256)]
    [InlineData(15 * 32 * 1024 - 256)]
    public async Task PersistenceProvider_Azure_WriteClearRead(int? stringLength)
    {
        var testName = string.Format("{0}({1} = {2})",
            nameof(PersistenceProvider_Azure_WriteClearRead),
            nameof(stringLength), stringLength == null ? "default" : stringLength.ToString());

        var grainState = TestStoreGrainState.NewRandomState(stringLength);

        var store = await InitializeStorage();

        await Test_PersistenceProvider_WriteClearRead(testName, store, grainState);
    }

    [SkippableTheory, TestCategory("Functional")]
    [InlineData(null)]
    [InlineData(15 * 32 * 1024 - 256)]
    public async Task PersistenceProvider_Azure_ChangeReadFormat(int? stringLength)
    {
        var testName = string.Format("{0}({1} = {2})",
            nameof(PersistenceProvider_Azure_ChangeReadFormat),
            nameof(stringLength), stringLength == null ? "default" : stringLength.ToString());

        var grainState = TestStoreGrainState.NewRandomState(stringLength);
        var grainId = GrainId.Create("testgrain", Guid.NewGuid().ToString());

        var store = await InitializeStorage();

        grainState = await Test_PersistenceProvider_WriteRead(testName, store, grainState, grainId);

        store = await InitializeStorage();

        await Test_PersistenceProvider_Read(testName, store, grainState, grainId);
    }

    [SkippableTheory, TestCategory("Functional")]
    [InlineData(null)]
    [InlineData(15 * 32 * 1024 - 256)]
    public async Task PersistenceProvider_Azure_ChangeWriteFormat(int? stringLength)
    {
        var testName = string.Format("{0}({1}={2})",
            nameof(PersistenceProvider_Azure_ChangeWriteFormat),
            nameof(stringLength), stringLength == null ? "default" : stringLength.ToString());

        var grainState = TestStoreGrainState.NewRandomState(stringLength);

        var grainId = GrainId.Create("testgrain", Guid.NewGuid().ToString());

        var store = await InitializeStorage();

        await Test_PersistenceProvider_WriteRead(testName, store, grainState, grainId);

        grainState = TestStoreGrainState.NewRandomState(stringLength);
        grainState.ETag = "*";

        store = await InitializeStorage();

        await Test_PersistenceProvider_WriteRead(testName, store, grainState, grainId);
    }

    private async Task Test_PersistenceProvider_Read(string grainTypeName, IGrainStorage store, GrainState<TestStoreGrainState> grainState, GrainId grainId)
    {
        grainState ??= new GrainState<TestStoreGrainState>(new TestStoreGrainState());

        var storedGrainState = new GrainState<TestStoreGrainState>(new TestStoreGrainState());

        Stopwatch sw = new Stopwatch();
        sw.Start();

        await store.ReadStateAsync(grainTypeName, grainId, storedGrainState);

        TimeSpan readTime = sw.Elapsed;
        output.WriteLine("{0} - Read time = {1}", store.GetType().FullName, readTime);

        var storedState = storedGrainState.State;
        Assert.Equal(grainState.State.A, storedState.A);
        Assert.Equal(grainState.State.B, storedState.B);
        Assert.Equal(grainState.State.C, storedState.C);
    }

    private async Task<GrainState<TestStoreGrainState>> Test_PersistenceProvider_WriteRead(string grainTypeName,
        IGrainStorage store, GrainState<TestStoreGrainState> grainState, GrainId grainId)
    {
        grainState ??= TestStoreGrainState.NewRandomState();

        Stopwatch sw = new Stopwatch();
        sw.Start();

        await store.WriteStateAsync(grainTypeName, grainId, grainState);

        TimeSpan writeTime = sw.Elapsed;
        sw.Restart();

        var storedGrainState = new GrainState<TestStoreGrainState>
        {
            State = new TestStoreGrainState()
        };
        await store.ReadStateAsync(grainTypeName, grainId, storedGrainState);
        TimeSpan readTime = sw.Elapsed;
        output.WriteLine("{0} - Write time = {1} Read time = {2}", store.GetType().FullName, writeTime, readTime);
        Assert.Equal(grainState.State.A, storedGrainState.State.A);
        Assert.Equal(grainState.State.B, storedGrainState.State.B);
        Assert.Equal(grainState.State.C, storedGrainState.State.C);

        return storedGrainState;
    }

    private async Task<GrainState<TestStoreGrainState>> Test_PersistenceProvider_WriteClearRead(string grainTypeName,
        IGrainStorage store, GrainState<TestStoreGrainState> grainState = null, GrainId grainId = default)
    {
        grainId = fixture.InternalGrainFactory.GetGrain(grainId.IsDefault ? LegacyGrainId.NewId().ToGrainId() : grainId).GetGrainId();

        if (grainState == null)
        {
            grainState = TestStoreGrainState.NewRandomState();
        }

        Stopwatch sw = new Stopwatch();
        sw.Start();

        await store.WriteStateAsync(grainTypeName, grainId, grainState);

        TimeSpan writeTime = sw.Elapsed;
        sw.Restart();

        await store.ClearStateAsync(grainTypeName, grainId, grainState);

        var storedGrainState = new GrainState<TestStoreGrainState>
        {
            State = new TestStoreGrainState()
        };
        await store.ReadStateAsync(grainTypeName, grainId, storedGrainState);
        TimeSpan readTime = sw.Elapsed;
        output.WriteLine("{0} - Write time = {1} Read time = {2}", store.GetType().FullName, writeTime, readTime);
        Assert.NotNull(storedGrainState.State);
        Assert.Equal(default(string), storedGrainState.State.A);
        Assert.Equal(default(int), storedGrainState.State.B);
        Assert.Equal(default(long), storedGrainState.State.C);

        return storedGrainState;
    }

    public class TestStoreGrainStateWithCustomJsonProperties
    {
        [JsonPropertyName("s")]
        public string String { get; set; }

        internal static GrainState<TestStoreGrainStateWithCustomJsonProperties> NewRandomState(int? aPropertyLength = null)
        {
            return new GrainState<TestStoreGrainStateWithCustomJsonProperties>
            {
                State = new TestStoreGrainStateWithCustomJsonProperties
                {
                    String = aPropertyLength == null
                        ? Random.Shared.Next().ToString(CultureInfo.InvariantCulture)
                        : GenerateRandomDigitString(aPropertyLength.Value)
                }
            };
        }

        private static string GenerateRandomDigitString(int stringLength)
        {
            var characters = new char[stringLength];
            for (var i = 0; i < stringLength; ++i)
            {
                characters[i] = (char)Random.Shared.Next('0', '9' + 1);
            }
            return new string(characters);
        }
    }
}