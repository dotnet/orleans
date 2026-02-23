using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Orleans.TestingHost;
using Tester;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace TestExtensions.Runners;

public abstract class GrainPersistenceTestsRunner : OrleansTestingBase
{
    private readonly ITestOutputHelper output;
    private readonly string grainNamespace;
    private readonly BaseTestClusterFixture fixture;
    protected readonly ILogger logger;
    protected TestCluster HostedCluster { get; private set; }
    private static readonly double timingFactor = TestUtils.CalibrateTimings();
    private const int LoopIterations_Grain = 1000;
    private const int BatchSize = 100;
    private const int MaxReadTime = 200;
    private const int MaxWriteTime = 2000;

    protected GrainPersistenceTestsRunner(ITestOutputHelper output, BaseTestClusterFixture fixture, string grainNamespace = "UnitTests.Grains")
    {
        this.output = output;
        this.fixture = fixture;
        this.grainNamespace = grainNamespace;
        logger = fixture.Logger;
        HostedCluster = fixture.HostedCluster;
    }

    protected bool DeleteStateOnClear { get; init; }
    protected bool IsDurableStorage { get; init; } = true;
    protected bool DistinguishesGenericGrainTypeParameters { get; init; } = true;

    protected TimeSpan PerformanceTestTimeout { get; init; } = TimeSpan.FromSeconds(90);

    public IGrainFactory GrainFactory => fixture.GrainFactory;

    [SkippableFact, TestCategory("Functional")]
    public async Task ClearStateAsync_Before_WriteStateAsync()
    {
        var grain = GrainFactory.GetGrain<IGrainStorageTestGrain>(Guid.NewGuid(), grainNamespace);

        await grain.DoDelete();

        var state = await grain.GetStateAsync();
        Assert.False(state.RecordExists);
        Assert.NotNull(state.State);
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_GrainStorage_Delete()
    {
        var grain = GrainFactory.GetGrain<IGrainStorageTestGrain>(Guid.NewGuid(), grainNamespace);

        await grain.DoWrite(1);
        var state = await grain.GetStateAsync();
        Assert.True(state.RecordExists);
        Assert.NotNull(state.ETag);
        Assert.NotNull(state.State);

        await grain.DoDelete();

        state = await grain.GetStateAsync();
        Assert.False(state.RecordExists);
        if (DeleteStateOnClear)
        {
            Assert.Null(state.ETag);
        }

        Assert.NotNull(state.State);

        var val = await grain.GetValue(); // Should this throw instead?
        Assert.Equal(0, val);  // "Value after Delete"

        await grain.DoWrite(2);

        val = await grain.GetValue();
        Assert.Equal(2, val);  // "Value after Delete + New Write"
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_GrainStorage_Read()
    {
        var id = Guid.NewGuid();
        var grain = GrainFactory.GetGrain<IGrainStorageTestGrain>(id, grainNamespace);

        var val = await grain.GetValue();

        Assert.Equal(0, val);  // "Initial value"

        var state = await grain.GetStateAsync();
        Assert.False(state.RecordExists);
        Assert.NotNull(state.State);
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_GuidKey_GrainStorage_Read_Write()
    {
        var id = Guid.NewGuid();
        var grain = GrainFactory.GetGrain<IGrainStorageTestGrain>(id, grainNamespace);

        var val = await grain.GetValue();

        Assert.Equal(0, val);  // "Initial value"

        await grain.DoWrite(1);
        val = await grain.GetValue();
        Assert.Equal(1, val);  // "Value after Write-1"

        await grain.DoWrite(2);
        val = await grain.GetValue();
        Assert.Equal(2, val);  // "Value after Write-2"

        val = await grain.DoRead();

        Assert.Equal(2, val);  // "Value after Re-Read"
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_LongKey_GrainStorage_Read_Write()
    {
        long id = Random.Shared.Next();
        var grain = GrainFactory.GetGrain<IGrainStorageTestGrain_LongKey>(id, grainNamespace);

        var val = await grain.GetValue();

        Assert.Equal(0, val);  // "Initial value"

        await grain.DoWrite(1);
        val = await grain.GetValue();
        Assert.Equal(1, val);  // "Value after Write-1"

        await grain.DoWrite(2);
        val = await grain.GetValue();
        Assert.Equal(2, val);  // "Value after Write-2"

        val = await grain.DoRead();

        Assert.Equal(2, val);  // "Value after Re-Read"
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_LongKeyExtended_GrainStorage_Read_Write()
    {
        long id = Random.Shared.Next();
        var extKey = Random.Shared.Next().ToString(CultureInfo.InvariantCulture);

        var
            grain = GrainFactory.GetGrain<IGrainStorageTestGrain_LongExtendedKey>(id, extKey, grainNamespace);

        var val = await grain.GetValue();

        Assert.Equal(0, val);  // "Initial value"

        await grain.DoWrite(1);
        val = await grain.GetValue();
        Assert.Equal(1, val);  // "Value after Write-1"

        await grain.DoWrite(2);
        val = await grain.GetValue();
        Assert.Equal(2, val);  // "Value after Write-2"

        val = await grain.DoRead();
        Assert.Equal(2, val);  // "Value after DoRead"

        val = await grain.GetValue();
        Assert.Equal(2, val);  // "Value after Re-Read"

        var extKeyValue = await grain.GetExtendedKeyValue();
        Assert.Equal(extKey, extKeyValue);  // "Extended Key"
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_GuidKeyExtended_GrainStorage_Read_Write()
    {
        var id = Guid.NewGuid();
        var extKey = Random.Shared.Next().ToString(CultureInfo.InvariantCulture);

        var
            grain = GrainFactory.GetGrain<IGrainStorageTestGrain_GuidExtendedKey>(id, extKey, grainNamespace);

        var val = await grain.GetValue();

        Assert.Equal(0, val);  // "Initial value"

        await grain.DoWrite(1);
        val = await grain.GetValue();
        Assert.Equal(1, val);  // "Value after Write-1"

        await grain.DoWrite(2);
        val = await grain.GetValue();
        Assert.Equal(2, val);  // "Value after Write-2"

        val = await grain.DoRead();
        Assert.Equal(2, val);  // "Value after DoRead"

        val = await grain.GetValue();
        Assert.Equal(2, val);  // "Value after Re-Read"

        var extKeyValue = await grain.GetExtendedKeyValue();
        Assert.Equal(extKey, extKeyValue);  // "Extended Key"
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_Generic_GrainStorage_Read_Write()
    {
        long id = Random.Shared.Next();

        var grain = GrainFactory.GetGrain<IGrainStorageGenericGrain<int>>(id, grainNamespace);

        var val = await grain.GetValue();

        Assert.Equal(0, val);  // "Initial value"

        await grain.DoWrite(1);
        val = await grain.GetValue();
        Assert.Equal(1, val);  // "Value after Write-1"

        await grain.DoWrite(2);
        val = await grain.GetValue();
        Assert.Equal(2, val);  // "Value after Write-2"

        val = await grain.DoRead();

        Assert.Equal(2, val);  // "Value after Re-Read"
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_NestedGeneric_GrainStorage_Read_Write()
    {
        long id = Random.Shared.Next();

        var grain = GrainFactory.GetGrain<IGrainStorageGenericGrain<List<int>>>(id, grainNamespace);

        var val = await grain.GetValue();

        Assert.Null(val);  // "Initial value"

        await grain.DoWrite(new List<int> { 1 });
        val = await grain.GetValue();
        Assert.Equal(new List<int> { 1 }, val);  // "Value after Write-1"

        await grain.DoWrite(new List<int> { 1, 2 });
        val = await grain.GetValue();
        Assert.Equal(new List<int> { 1, 2 }, val);  // "Value after Write-2"

        val = await grain.DoRead();

        Assert.Equal(new List<int> { 1, 2 }, val);  // "Value after Re-Read"
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_Generic_GrainStorage_DiffTypes()
    {
        if (!DistinguishesGenericGrainTypeParameters)
        {
            throw new SkipException("This provider does not support distinguishing grains in storage by their generic type parameters.");
        }

        long id1 = Random.Shared.Next();
        var id2 = id1;
        var id3 = id1;

        var grain1 = GrainFactory.GetGrain<IGrainStorageGenericGrain<int>>(id1, grainNamespace);

        var grain2 = GrainFactory.GetGrain<IGrainStorageGenericGrain<string>>(id2, grainNamespace);

        var grain3 = GrainFactory.GetGrain<IGrainStorageGenericGrain<double>>(id3, grainNamespace);

        var val1 = await grain1.GetValue();
        Assert.Equal(0, val1);  // "Initial value - 1"

        var val2 = await grain2.GetValue();
        Assert.Null(val2);  // "Initial value - 2"

        var val3 = await grain3.GetValue();
        Assert.Equal(0.0, val3);  // "Initial value - 3"

        var expected1 = 1;
        await grain1.DoWrite(expected1);
        val1 = await grain1.GetValue();
        Assert.Equal(expected1, val1);  // "Value after Write#1 - 1"

        var expected2 = "Three";
        await grain2.DoWrite(expected2);
        val2 = await grain2.GetValue();
        Assert.Equal(expected2, val2);  // "Value after Write#1 - 2"

        var expected3 = 5.1;
        await grain3.DoWrite(expected3);
        val3 = await grain3.GetValue();
        Assert.Equal(expected3, val3);  // "Value after Write#1 - 3"

        val1 = await grain1.GetValue();
        Assert.Equal(expected1, val1);  // "Value before Write#2 - 1"
        expected1 = 2;
        await grain1.DoWrite(expected1);
        val1 = await grain1.GetValue();
        Assert.Equal(expected1, val1);  // "Value after Write#2 - 1"
        val1 = await grain1.DoRead();
        Assert.Equal(expected1, val1);  // "Value after Re-Read - 1"

        val2 = await grain2.GetValue();
        Assert.Equal(expected2, val2);  // "Value before Write#2 - 2"
        expected2 = "Four";
        await grain2.DoWrite(expected2);
        val2 = await grain2.GetValue();
        Assert.Equal(expected2, val2);  // "Value after Write#2 - 2"
        val2 = await grain2.DoRead();
        Assert.Equal(expected2, val2);  // "Value after Re-Read - 2"

        val3 = await grain3.GetValue();
        Assert.Equal(expected3, val3);  // "Value before Write#2 - 3"
        expected3 = 6.2;
        await grain3.DoWrite(expected3);
        val3 = await grain3.GetValue();
        Assert.Equal(expected3, val3);  // "Value after Write#2 - 3"
        val3 = await grain3.DoRead();
        Assert.Equal(expected3, val3);  // "Value after Re-Read - 3"
    }
    
    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_GrainStorage_SiloRestart()
    {
        if (!IsDurableStorage) 
        {
            throw new SkipException("This provider does not persist state, so cannot survive a silo restart.");
        }

        var initialServiceId = fixture.GetClientServiceId();

        output.WriteLine("ClusterId={0} ServiceId={1}", HostedCluster.Options.ClusterId, initialServiceId);

        var id1 = Guid.NewGuid();
        var grain1 = GrainFactory.GetGrain<IGrainStorageTestGrain>(id1, grainNamespace);
        var id2 = Guid.NewGuid();
        var grain2 = GrainFactory.GetGrain<IGrainStorageTestGrain>(id2, grainNamespace);

        var val = await grain1.GetValue();
        Assert.Equal(0, val);  // "Initial value"

        await grain1.DoWrite(1);

        await grain2.DoWrite(1);

        val = await grain1.GetValue();
        Assert.Equal(1, val);
        val = await grain2.GetValue();
        Assert.Equal(1, val);

        await grain2.DoDelete();

        var serviceId = await GrainFactory.GetGrain<IServiceIdGrain>(Guid.Empty).GetServiceId();
        Assert.Equal(initialServiceId, serviceId);  // "ServiceId same before restart."

        output.WriteLine("About to reset Silos");
        foreach (var silo in HostedCluster.GetActiveSilos().ToList())
        {
            await HostedCluster.RestartSiloAsync(silo);
        }

        await HostedCluster.InitializeClientAsync();

        output.WriteLine("Silos restarted");

        serviceId = await GrainFactory.GetGrain<IServiceIdGrain>(Guid.Empty).GetServiceId();
        grain1 = GrainFactory.GetGrain<IGrainStorageTestGrain>(id1, grainNamespace);
        grain2 = GrainFactory.GetGrain<IGrainStorageTestGrain>(id2, grainNamespace);
        output.WriteLine("ClusterId={0} ServiceId={1}", HostedCluster.Options.ClusterId, serviceId);
        Assert.Equal(initialServiceId, serviceId);  // "ServiceId same after restart."

        val = await grain1.GetValue();
        Assert.Equal(1, val);  // "Value after Write-1"

        val = await grain2.GetValue();
        Assert.Equal(0, val);  // State should be cleared.

        await grain1.DoWrite(2);
        val = await grain1.GetValue();
        Assert.Equal(2, val);  // "Value after Write-2"

        val = await grain1.DoRead();

        Assert.Equal(2, val);  // "Value after Re-Read"
    }

    [SkippableFact, TestCategory("CorePerf"), TestCategory("Performance"), TestCategory("Stress")]
    public async Task Persistence_Perf_Activate()
    {
        const string testName = "Persistence_Perf_Activate";
        int n = LoopIterations_Grain;
        TimeSpan target = TimeSpan.FromMilliseconds(MaxReadTime * n);

        // Timings for Activate
        await RunPerfTest(n, testName, target,
            grainNoState => grainNoState.PingAsync(),
            grainMemory => grainMemory.DoSomething(),
            grainMemoryStore => grainMemoryStore.GetValue(),
            grainAzureStore => grainAzureStore.GetValue());
    }

    [SkippableFact, TestCategory("CorePerf"), TestCategory("Performance"), TestCategory("Stress")]
    public async Task Persistence_Perf_Write()
    {
        const string testName = "Persistence_Perf_Write";
        int n = LoopIterations_Grain;
        TimeSpan target = TimeSpan.FromMilliseconds(MaxWriteTime * n);

        // Timings for Write
        await RunPerfTest(n, testName, target,
            grainNoState => grainNoState.EchoAsync(testName),
            grainMemory => grainMemory.DoWrite(n),
            grainMemoryStore => grainMemoryStore.DoWrite(n),
            grainAzureStore => grainAzureStore.DoWrite(n));
    }

    [SkippableFact, TestCategory("CorePerf"), TestCategory("Performance"), TestCategory("Stress")]
    public async Task Persistence_Perf_Write_Reread()
    {
        const string testName = "Persistence_Perf_Write_Read";
        int n = LoopIterations_Grain;
        TimeSpan target = TimeSpan.FromMilliseconds(MaxWriteTime * n);

        // Timings for Write
        await RunPerfTest(n, testName + "--Write", target,
            grainNoState => grainNoState.EchoAsync(testName),
            grainMemory => grainMemory.DoWrite(n),
            grainMemoryStore => grainMemoryStore.DoWrite(n),
            grainAzureStore => grainAzureStore.DoWrite(n));

        // Timings for Activate
        await RunPerfTest(n, testName + "--ReRead", target,
            grainNoState => grainNoState.GetLastEchoAsync(),
            grainMemory => grainMemory.DoRead(),
            grainMemoryStore => grainMemoryStore.DoRead(),
            grainAzureStore => grainAzureStore.DoRead());
    }

    protected async Task Persistence_Silo_StorageProvider(string providerName)
    {
        List<SiloHandle> silos = this.HostedCluster.GetActiveSilos().ToList();
        foreach (var silo in silos)
        {
            var isPresent = await this.HostedCluster.Client.GetTestHooks(silo).HasStorageProvider(providerName);
            Assert.True(isPresent, $"No storage provider found: {providerName}");
        }
    }

    // ---------- Utility functions ----------

    private async Task RunPerfTest(int n, string testName, TimeSpan target,
        Func<IEchoTaskGrain, Task> actionNoState,
        Func<IPersistenceTestGrain, Task> actionMemory,
        Func<IMemoryStorageTestGrain, Task> actionMemoryStore,
        Func<IGrainStorageTestGrain, Task> actionAzureTable)
    {
        IEchoTaskGrain[] noStateGrains = new IEchoTaskGrain[n];
        IPersistenceTestGrain[] memoryGrains = new IPersistenceTestGrain[n];
        IGrainStorageTestGrain[] azureStoreGrains = new IGrainStorageTestGrain[n];
        IMemoryStorageTestGrain[] memoryStoreGrains = new IMemoryStorageTestGrain[n];

        for (int i = 0; i < n; i++)
        {
            Guid id = Guid.NewGuid();
            noStateGrains[i] = this.GrainFactory.GetGrain<IEchoTaskGrain>(id);
            memoryGrains[i] = this.GrainFactory.GetGrain<IPersistenceTestGrain>(id);
            azureStoreGrains[i] = this.GrainFactory.GetGrain<IGrainStorageTestGrain>(id);
            memoryStoreGrains[i] = this.GrainFactory.GetGrain<IMemoryStorageTestGrain>(id);
        }

        TimeSpan baseline, elapsed;

        elapsed = baseline = await TestUtils.TimeRunAsync(n, TimeSpan.Zero, testName + " (No state)",
            () => RunIterations(testName, n, i => actionNoState(noStateGrains[i])));

        elapsed = await TestUtils.TimeRunAsync(n, baseline, testName + " (Local Memory Store)",
            () => RunIterations(testName, n, i => actionMemory(memoryGrains[i])));

        elapsed = await TestUtils.TimeRunAsync(n, baseline, testName + " (Dev Store Grain Store)",
            () => RunIterations(testName, n, i => actionMemoryStore(memoryStoreGrains[i])));

        elapsed = await TestUtils.TimeRunAsync(n, baseline, testName + " (Azure Table Store)",
            () => RunIterations(testName, n, i => actionAzureTable(azureStoreGrains[i])));

        if (elapsed > target.Multiply(timingFactor))
        {
            string msg = string.Format("{0}: Elapsed time {1} exceeds target time {2}", testName, elapsed, target);

            if (elapsed > target.Multiply(2.0 * timingFactor))
            {
                Assert.Fail(msg);
            }
            else
            {
                throw new SkipException(msg);
            }
        }
    }

    private async Task RunIterations(string testName, int n, Func<int, Task> action)
    {
        List<Task> promises = new List<Task>();
        Stopwatch sw = Stopwatch.StartNew();
        // Fire off requests in batches
        for (int i = 0; i < n; i++)
        {
            var promise = action(i);
            promises.Add(promise);
            if ((i % BatchSize) == 0 && i > 0)
            {
                await Task.WhenAll([.. promises]).WaitAsync(PerformanceTestTimeout);
                promises.Clear();
                //output.WriteLine("{0} has done {1} iterations  in {2} at {3} RPS",
                //                  testName, i, sw.Elapsed, i / sw.Elapsed.TotalSeconds);
            }
        }

        await Task.WhenAll([.. promises]).WaitAsync(PerformanceTestTimeout);
        sw.Stop();
        output.WriteLine("{0} completed. Did {1} iterations in {2} at {3} RPS",
                          testName, n, sw.Elapsed, n / sw.Elapsed.TotalSeconds);
    }
}
