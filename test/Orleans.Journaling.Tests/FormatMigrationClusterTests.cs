using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Core.Internal;
using Orleans.Journaling.Json;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class FormatMigrationClusterTests
{
    [Fact]
    public async Task SiloRollingFormatMigration_RecoversBinaryJournalOnJsonSilo()
    {
        var storageProvider = new SharedVolatileJournalStorageProvider();
        var builder = new InProcessTestClusterBuilder(1);
        builder.Options.AssumeHomogenousSilosForTesting = false;
        builder.ConfigureSilo((siloOptions, siloBuilder) =>
        {
            var isInitialBinarySilo = string.Equals(siloOptions.SiloName, "Silo_0", StringComparison.Ordinal);
            siloBuilder.AddJournalStorage();
            siloBuilder.UseJsonJournalFormat(JournalingTestsJsonContext.Default);
            siloBuilder.Services.Configure<JournaledStateManagerOptions>(options =>
            {
                options.JournalFormatKey = isInitialBinarySilo
                    ? OrleansBinaryJournalFormat.JournalFormatKey
                    : JsonJournalExtensions.JournalFormatKey;
            });
            siloBuilder.Services.AddSingleton(storageProvider);
            siloBuilder.Services.AddScoped<IJournalStorageProvider>(sp => new SharedVolatileJournalStorageProviderAdapter(
                storageProvider,
                sp.GetRequiredService<IOptions<JournaledStateManagerOptions>>()));
        });
        var cluster = builder.Build();

        try
        {
            await cluster.DeployAsync();
            var binarySilo = Assert.Single(cluster.Silos);
            var grainId = Guid.NewGuid();
            var grain = cluster.Client.GetGrain<ITestDurableGrain>(grainId);

            await grain.SetTestValues("binary", 1);
            Assert.Equal("binary", await grain.GetName());
            Assert.Equal(1, await grain.GetCounter());
            Assert.Contains(OrleansBinaryJournalFormat.JournalFormatKey, storageProvider.StoredFormatKeys);

            var jsonSilo = await cluster.StartAdditionalSiloAsync();
            await cluster.WaitForLivenessToStabilizeAsync();
            await cluster.StopSiloAsync(binarySilo);
            await cluster.WaitForLivenessToStabilizeAsync();
            await cluster.InitializeClientAsync();

            grain = cluster.Client.GetGrain<ITestDurableGrain>(grainId);
            Assert.Equal("binary", await grain.GetName());
            Assert.Equal(1, await grain.GetCounter());

            await grain.SetTestValues("json", 2);
            await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();

            grain = cluster.Client.GetGrain<ITestDurableGrain>(grainId);
            Assert.Equal("json", await grain.GetName());
            Assert.Equal(2, await grain.GetCounter());
            Assert.Contains(JsonJournalExtensions.JournalFormatKey, storageProvider.StoredFormatKeys);
            Assert.Contains(jsonSilo, cluster.Silos);
        }
        finally
        {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    private sealed class SharedVolatileJournalStorageProvider
    {
        private readonly ConcurrentDictionary<GrainId, VolatileJournalStorage> _storage = new();

        public IEnumerable<string?> StoredFormatKeys => _storage.Values.Select(static storage => storage.StoredJournalFormatKey);

        public IJournalStorage Create(IGrainContext grainContext, IOptions<JournaledStateManagerOptions> options)
        {
            var journalFormatKey = JournalFormatServices.ValidateJournalFormatKey(options.Value.JournalFormatKey);
            var storage = _storage.GetOrAdd(grainContext.GrainId, _ => new VolatileJournalStorage(journalFormatKey));
            storage.SetConfiguredJournalFormatKey(journalFormatKey);
            return storage;
        }

    }

    private sealed class SharedVolatileJournalStorageProviderAdapter(
        SharedVolatileJournalStorageProvider provider,
        IOptions<JournaledStateManagerOptions> options) : IJournalStorageProvider
    {
        public IJournalStorage CreateStorage(IGrainContext grainContext) => provider.Create(grainContext, options);
    }
}
