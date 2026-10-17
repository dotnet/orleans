using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Orleans.Journaling.Ranges;
using Orleans.Journaling.Tests;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Runs the Orleans.Journaling durable collection and state manager suites against Garnet journal storage.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class GarnetJournalBatchTests : JournalBatchTests
{
    private readonly string _directory = GarnetPartitionFixture.CreateTemporaryDirectory();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.Configure<GarnetJournalStorageOptions>(options =>
        {
            options.DataDirectory = _directory;
            options.MetaStore = new InMemoryRangeMetaStore();
            options.HostId = GarnetPartitionFixture.HostId;
            options.LogMemorySize = "64m";
            options.IndexMemorySize = "1m";
        });
        services.AddSingleton<GarnetJournalStorageProvider>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, GarnetJournalStorageProvider>();
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(new DirectoryCleanup(_directory));
    }

    protected override IJournalStorage CreateStorage(IServiceProvider serviceProvider, IGrainContext grainContext) =>
        serviceProvider.GetRequiredService<GarnetJournalStorageProvider>().CreateStorage(JournalId.FromGrainId(grainContext.GrainId));

    /// <summary>Deletes the partition's directory after the provider stops.</summary>
    private sealed class DirectoryCleanup(string directory) : ILifecycleParticipant<ISiloLifecycle>
    {
        public void Participate(ISiloLifecycle lifecycle) => lifecycle.Subscribe(
            nameof(DirectoryCleanup),
            ServiceLifecycleStage.RuntimeInitialize - 1,
            onStart: _ => Task.CompletedTask,
            onStop: _ =>
            {
                GarnetPartitionFixture.TryDeleteDirectory(directory);
                return Task.CompletedTask;
            });
    }
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class GarnetDurableDictionaryTests(GarnetPartitionFixture fixture) : DurableDictionaryTests, IClassFixture<GarnetPartitionFixture>
{
    protected override IJournalStorage CreateStorage() => GarnetSuiteStorage.Create(fixture, ManagerOptions);
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class GarnetDurableQueueTests(GarnetPartitionFixture fixture) : DurableQueueTests, IClassFixture<GarnetPartitionFixture>
{
    protected override IJournalStorage CreateStorage() => GarnetSuiteStorage.Create(fixture, ManagerOptions);
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class GarnetDurableSetTests(GarnetPartitionFixture fixture) : DurableSetTests, IClassFixture<GarnetPartitionFixture>
{
    protected override IJournalStorage CreateStorage() => GarnetSuiteStorage.Create(fixture, ManagerOptions);
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class GarnetDurableValueTests(GarnetPartitionFixture fixture) : DurableValueTests, IClassFixture<GarnetPartitionFixture>
{
    protected override IJournalStorage CreateStorage() => GarnetSuiteStorage.Create(fixture, ManagerOptions);
}

internal static class GarnetSuiteStorage
{
    public static IJournalStorage Create(GarnetPartitionFixture fixture, JournaledStateManagerOptions options)
        => new GarnetJournalStorage(
            fixture.Provider.Router,
            JournalId.Create("suite", Guid.NewGuid().ToString("N")),
            options.JournalFormatKey!,
            GarnetJournalStorageOptions.DEFAULT_COMPACTION_THRESHOLD_BYTES);
}
