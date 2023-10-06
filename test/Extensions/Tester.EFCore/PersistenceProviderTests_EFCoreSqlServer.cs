using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Persistence;
using Orleans.Persistence.EntityFrameworkCore.SqlServer.Data;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;
using TestExtensions;
using UnitTests.Persistence;
using Xunit.Abstractions;

namespace Tester.EFCore;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("Persistence"), TestCategory("EFCore"), TestCategory("EFCore-SqlServer")]
public class PersistenceProviderTests_EFCoreSqlServer
{
    private readonly IProviderRuntime _providerRuntime;
    private readonly ITestOutputHelper _output;
    private readonly TestEnvironmentFixture _fixture;
    private readonly string _clusterId;
    private readonly string _serviceId;

    public PersistenceProviderTests_EFCoreSqlServer(
        ITestOutputHelper output,
        TestEnvironmentFixture fixture)
    {
        EFCoreTestUtils.CheckSqlServer();

        this._output = output;
        this._fixture = fixture;
        this._providerRuntime = new ClientProviderRuntime(
            this._fixture.InternalGrainFactory,
            this._fixture.Services,
            this._fixture.Services.GetRequiredService<ClientGrainContext>());
        this._clusterId = Guid.NewGuid().ToString("N");
        this._serviceId = Guid.NewGuid().ToString("N");
    }

    private async Task<EFGrainStorage<SqlServerGrainStateDbContext, byte[]>> InitializeStorage()
    {
        var clusterOptions = Options.Create(new ClusterOptions {ClusterId = _clusterId, ServiceId = _serviceId});
        var loggerFactory = this._providerRuntime.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var lifecycle = ActivatorUtilities.CreateInstance<SiloLifecycleSubject>(this._providerRuntime.ServiceProvider);

        var cs = "Server=localhost,1433;Database=OrleansTests.Generic;User Id=sa;Password=yourStrong(!)Password;TrustServerCertificate=True";

        var sp = new ServiceCollection()
            .AddPooledDbContextFactory<SqlServerGrainStateDbContext>(optionsBuilder =>
            {
                optionsBuilder.UseSqlServer(cs, opt =>
                {
                    opt.MigrationsHistoryTable("__EFMigrationsHistory");
                    opt.MigrationsAssembly(typeof(SqlServerGrainStateDbContext).Assembly.FullName);
                });
            }).BuildServiceProvider();

        var factory = sp.GetRequiredService<IDbContextFactory<SqlServerGrainStateDbContext>>();

        var ctx = factory.CreateDbContext();
        if ((await ctx.Database.GetPendingMigrationsAsync()).Any())
        {
            try
            {
                await ctx.Database.MigrateAsync();
            }
            catch { }
        }

        var store = new EFGrainStorage<SqlServerGrainStateDbContext, byte[]>("TestStorage",
            loggerFactory,
            clusterOptions,
            factory,
            new SqlServerGrainStateETagConverter(),
            this._providerRuntime.ServiceProvider);

        store.Participate(lifecycle);

        await lifecycle.OnStart();

        return store;
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task PersistenceProvider_Read()
    {
        const string testName = nameof(PersistenceProvider_Read);

        var store = await InitializeStorage();
        await Test_PersistenceProvider_Read(testName, store, null, grainId: GrainId.Create("TestGrain", Guid.NewGuid().ToString()));
    }

    [SkippableTheory, TestCategory("Functional")]
    [InlineData(null)]
    [InlineData(15 * 64 * 1024 - 256)]
    [InlineData(15 * 32 * 1024 - 256)]
    public async Task PersistenceProvider_WriteRead(int? stringLength)
    {
        var testName = string.Format("{0}({1} = {2})",
            nameof(PersistenceProvider_WriteRead),
            nameof(stringLength), stringLength == null ? "default" : stringLength.ToString());

        var grainState = TestStoreGrainState.NewRandomState(stringLength);

        var store = await InitializeStorage();

        await Test_PersistenceProvider_WriteRead(testName, store, grainState, GrainId.Create("TestGrain", Guid.NewGuid().ToString()));
    }

    [SkippableTheory, TestCategory("Functional")]
    [InlineData(null)]
    [InlineData(15 * 64 * 1024 - 256)]
    [InlineData(15 * 32 * 1024 - 256)]
    public async Task PersistenceProvider_WriteClearRead(int? stringLength)
    {
        var testName = string.Format("{0}({1} = {2})",
            nameof(PersistenceProvider_WriteClearRead),
            nameof(stringLength), stringLength == null ? "default" : stringLength.ToString());

        var grainState = TestStoreGrainState.NewRandomState(stringLength);

        var store = await InitializeStorage();

        await Test_PersistenceProvider_WriteClearRead(testName, store, grainState);
    }

    [SkippableTheory, TestCategory("Functional")]
    [InlineData(null)]
    [InlineData(15 * 32 * 1024 - 256)]
    public async Task PersistenceProvider_ChangeReadFormat(int? stringLength)
    {
        var testName = string.Format("{0}({1} = {2})",
            nameof(PersistenceProvider_ChangeReadFormat),
            nameof(stringLength), stringLength == null ? "default" : stringLength.ToString());

        var grainState = TestStoreGrainState.NewRandomState(stringLength);
        var grainId = GrainId.Create("TestGrain", Guid.NewGuid().ToString());

        var store = await InitializeStorage();

        grainState = await Test_PersistenceProvider_WriteRead(testName, store, grainState, grainId);

        store = await InitializeStorage();

        await Test_PersistenceProvider_Read(testName, store, grainState, grainId);
    }

    [SkippableTheory, TestCategory("Functional")]
    [InlineData(null)]
    [InlineData(15 * 32 * 1024 - 256)]
    public async Task PersistenceProvider_ChangeWriteFormat(int? stringLength)
    {
        var testName = string.Format("{0}({1}={2})",
            nameof(PersistenceProvider_ChangeWriteFormat),
            nameof(stringLength), stringLength == null ? "default" : stringLength.ToString());

        var grainState = TestStoreGrainState.NewRandomState(stringLength);

        var grainId = GrainId.Create("TestGrain", Guid.NewGuid().ToString());

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

        var sw = new Stopwatch();
        sw.Start();

        await store.ReadStateAsync(grainTypeName, grainId, storedGrainState);

        var readTime = sw.Elapsed;
        this._output.WriteLine("{0} - Read time = {1}", store.GetType().FullName, readTime);

        var storedState = storedGrainState.State;
        Assert.Equal(grainState.State.A, storedState.A);
        Assert.Equal(grainState.State.B, storedState.B);
        Assert.Equal(grainState.State.C, storedState.C);
    }

    private async Task<GrainState<TestStoreGrainState>> Test_PersistenceProvider_WriteRead(string grainTypeName,
        IGrainStorage store, GrainState<TestStoreGrainState> grainState, GrainId grainId)
    {
        grainState ??= TestStoreGrainState.NewRandomState();

        var sw = new Stopwatch();
        sw.Start();

        await store.WriteStateAsync(grainTypeName, grainId, grainState);

        var writeTime = sw.Elapsed;
        sw.Restart();

        var storedGrainState = new GrainState<TestStoreGrainState> {State = new TestStoreGrainState()};
        await store.ReadStateAsync(grainTypeName, grainId, storedGrainState);
        var readTime = sw.Elapsed;
        this._output.WriteLine("{0} - Write time = {1} Read time = {2}", store.GetType().FullName, writeTime, readTime);
        Assert.Equal(grainState.State.A, storedGrainState.State.A);
        Assert.Equal(grainState.State.B, storedGrainState.State.B);
        Assert.Equal(grainState.State.C, storedGrainState.State.C);

        return storedGrainState;
    }

    private async Task Test_PersistenceProvider_WriteClearRead(string grainTypeName,
        IGrainStorage store, GrainState<TestStoreGrainState> grainState = null, GrainId grainId = default)
    {
        grainId = this._fixture.InternalGrainFactory.GetGrain(grainId.IsDefault ? LegacyGrainId.NewId().ToGrainId() : grainId).GetGrainId();

        grainState ??= TestStoreGrainState.NewRandomState();

        var sw = new Stopwatch();
        sw.Start();

        await store.WriteStateAsync(grainTypeName, grainId, grainState);

        var writeTime = sw.Elapsed;
        sw.Restart();

        await store.ClearStateAsync(grainTypeName, grainId, grainState);

        var storedGrainState = new GrainState<TestStoreGrainState> {State = new TestStoreGrainState()};
        await store.ReadStateAsync(grainTypeName, grainId, storedGrainState);
        var readTime = sw.Elapsed;
        this._output.WriteLine("{0} - Write time = {1} Read time = {2}", store.GetType().FullName, writeTime, readTime);
        Assert.NotNull(storedGrainState.State);
        Assert.Equal(default, storedGrainState.State.A);
        Assert.Equal(default, storedGrainState.State.B);
        Assert.Equal(default, storedGrainState.State.C);
    }

    public class TestStoreGrainStateWithCustomJsonProperties
    {
        [JsonPropertyName("s")] public string String { get; set; }

        internal static GrainState<TestStoreGrainStateWithCustomJsonProperties> NewRandomState(int? aPropertyLength = null) =>
            new()
            {
                State = new TestStoreGrainStateWithCustomJsonProperties
                {
                    String = aPropertyLength == null
                        ? Random.Shared.Next().ToString(CultureInfo.InvariantCulture)
                        : GenerateRandomDigitString(aPropertyLength.Value)
                }
            };

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