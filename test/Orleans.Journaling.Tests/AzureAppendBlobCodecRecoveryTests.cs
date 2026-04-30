using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Internal;
using Orleans.Core;
using Orleans.Serialization;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("AzureStorage"), TestCategory("Functional")]
public sealed class AzureAppendBlobCodecRecoveryTests : JournalingTestBase, IAsyncLifetime
{
    private ServiceProvider _azureServiceProvider = null!;
    private SiloLifecycleSubject _siloLifecycle = null!;
    private ILogStorageProvider _storageProvider = null!;

    public AzureAppendBlobCodecRecoveryTests()
    {
        JournalingAzureStorageTestConfiguration.CheckPreconditionsOrThrow();
    }

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AzureAppendBlobLogStorageOptions>(options => JournalingAzureStorageTestConfiguration.ConfigureTestDefaults(options));
        services.AddSingleton<AzureAppendBlobLogStorageProvider>();
        services.AddFromExisting<ILogStorageProvider, AzureAppendBlobLogStorageProvider>();
        services.AddFromExisting<ILogFormatKeyProvider, AzureAppendBlobLogStorageProvider>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, AzureAppendBlobLogStorageProvider>();

        _azureServiceProvider = services.BuildServiceProvider();
        _storageProvider = _azureServiceProvider.GetRequiredService<ILogStorageProvider>();
        _siloLifecycle = new SiloLifecycleSubject(_azureServiceProvider.GetRequiredService<ILogger<SiloLifecycleSubject>>());

        foreach (var participant in _azureServiceProvider.GetServices<ILifecycleParticipant<ISiloLifecycle>>())
        {
            participant.Participate(_siloLifecycle);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await _siloLifecycle.OnStart(cts.Token);
    }

    public async Task DisposeAsync()
    {
        if (_siloLifecycle is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await _siloLifecycle.OnStop(cts.Token);
        }

        await _azureServiceProvider.DisposeAsync();
    }

    [SkippableFact]
    public async Task AzureAppendBlobStorage_AllDurableTypes_RecoverWithBinaryCodec()
    {
        var grainId = GrainId.Create("journaling-codec-recovery", Guid.NewGuid().ToString("N"));
        var storage = _storageProvider.Create(new LogSegmentTests.TestGrainContext(grainId));
        var first = CreateStateMachines(storage);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await first.Manager.InitializeAsync(cts.Token);

        first.Dictionary.Add("alpha", 1);
        first.Dictionary.Add("beta", 2);
        first.List.Add("one");
        first.List.Add("two");
        first.Queue.Enqueue("first");
        first.Queue.Enqueue("second");
        first.Set.Add("a");
        first.Set.Add("b");
        first.Value.Value = 42;
        ((IStorage<string>)first.State).State = "state-value";
        Assert.True(first.Tcs.TrySetResult(17));
        await first.Manager.WriteStateAsync(cts.Token);

        var recoveredStorage = _storageProvider.Create(new LogSegmentTests.TestGrainContext(grainId));
        var recovered = CreateStateMachines(recoveredStorage);
        await recovered.Manager.InitializeAsync(cts.Token);

        Assert.Equal(2, recovered.Dictionary.Count);
        Assert.Equal(1, recovered.Dictionary["alpha"]);
        Assert.Equal(2, recovered.Dictionary["beta"]);
        Assert.Equal(["one", "two"], recovered.List);
        Assert.Equal(2, recovered.Queue.Count);
        Assert.Equal("first", recovered.Queue.Dequeue());
        Assert.Equal("second", recovered.Queue.Dequeue());
        Assert.True(recovered.Set.SetEquals(["a", "b"]));
        Assert.Equal(42, recovered.Value.Value);
        Assert.Equal("state-value", ((IStorage<string>)recovered.State).State);
        Assert.Equal(DurableTaskCompletionSourceStatus.Completed, recovered.Tcs.State.Status);
        Assert.Equal(17, recovered.Tcs.State.Value);
        Assert.Equal(17, await recovered.Tcs.Task);
    }

    private DurableStateMachines CreateStateMachines(ILogStorage storage)
    {
        var manager = CreateManager(storage);
        return new DurableStateMachines(
            manager,
            new DurableDictionary<string, int>("dict", manager, new OrleansBinaryDictionaryOperationCodec<string, int>(ValueCodec<string>(), ValueCodec<int>())),
            new DurableList<string>("list", manager, new OrleansBinaryListOperationCodec<string>(ValueCodec<string>())),
            new DurableQueue<string>("queue", manager, new OrleansBinaryQueueOperationCodec<string>(ValueCodec<string>())),
            new DurableSet<string>("set", manager, new OrleansBinarySetOperationCodec<string>(ValueCodec<string>())),
            new DurableValue<int>("value", manager, new OrleansBinaryValueOperationCodec<int>(ValueCodec<int>())),
            new DurableState<string>("state", manager, new OrleansBinaryStateOperationCodec<string>(ValueCodec<string>())),
            new DurableTaskCompletionSource<int>(
                "tcs",
                manager,
                new OrleansBinaryTcsOperationCodec<int>(ValueCodec<int>(), ValueCodec<Exception>()),
                Copier<int>(),
                Copier<Exception>()));
    }

    private LogManager CreateManager(ILogStorage storage)
        => new(
            storage,
            LoggerFactory.CreateLogger<LogManager>(),
            Options.Create(ManagerOptions),
            new OrleansBinaryDictionaryOperationCodec<string, ulong>(ValueCodec<string>(), ValueCodec<ulong>()),
            new OrleansBinaryDictionaryOperationCodec<string, DateTime>(ValueCodec<string>(), ValueCodec<DateTime>()),
            TimeProvider.System);

    private ILogValueCodec<T> ValueCodec<T>() => new OrleansLogValueCodec<T>(CodecProvider.GetCodec<T>(), SessionPool);

    private DeepCopier<T> Copier<T>() => ServiceProvider.GetRequiredService<DeepCopier<T>>();

    private sealed record DurableStateMachines(
        LogManager Manager,
        DurableDictionary<string, int> Dictionary,
        DurableList<string> List,
        DurableQueue<string> Queue,
        DurableSet<string> Set,
        DurableValue<int> Value,
        DurableState<string> State,
        DurableTaskCompletionSource<int> Tcs);
}
