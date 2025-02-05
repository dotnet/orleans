using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Persistence.Cosmos.TypeInfo;
using Orleans.Persistence.Migration;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Persistence.Cosmos.Migration
{
    /// <summary>
    /// Is a wrapper over <see cref="CosmosGrainStorage"/>.
    /// Also contains the logic to migrate grain state from one storage to another.
    /// </summary>
    internal class MigrationAzureCosmosGrainStorage : IMigrationGrainStorage, ILifecycleParticipant<ISiloLifecycle>
    {
        private ILogger logger;
        private readonly string name;

        private readonly IGrainStateTypeInfoProvider grainStateTypeInfoProvider;
        private readonly CosmosGrainStorage cosmosGrainStorage;

        public MigrationAzureCosmosGrainStorage(
            string name,
            CosmosGrainStorage cosmosGrainStorage,
            IGrainStateTypeInfoProvider grainStateTypeInfoProvider,
            ILogger<MigrationAzureCosmosGrainStorage> logger)
        {
            this.name = name;
            this.cosmosGrainStorage = cosmosGrainStorage;
            this.grainStateTypeInfoProvider = grainStateTypeInfoProvider;
            this.logger = logger;
        }

        public void Participate(ISiloLifecycle lifecycle) => this.cosmosGrainStorage.Participate(lifecycle);
        public IAsyncEnumerable<StorageEntry> GetAll(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ClearStateAsync(string stateName, GrainReference grainReference, IGrainState grainState)
            => this.cosmosGrainStorage.ClearStateAsync(stateName, grainReference, grainState);

        public Task ReadStateAsync(string stateName, GrainReference grainReference, IGrainState grainState)
            => this.cosmosGrainStorage.ReadStateAsync(stateName, grainReference, grainState);

        public Task WriteStateAsync(string stateName, GrainReference grainReference, IGrainState grainState)
            => this.cosmosGrainStorage.WriteStateAsync(stateName, grainReference, grainState);

        public Task MigrateGrainStateAsync(string stateName, GrainReference grainReference, IGrainState grainState)
        {
            var grainTypeData = this.grainStateTypeInfoProvider.GetGrainStateTypeInfo(cosmosGrainStorage, grainReference, grainState);
            return grainTypeData.WriteStateAsync(stateName, grainReference, grainState);
        }
    }

    public static class MigrationAzureCosmosGrainStorageFactory
    {
        public static IMigrationGrainStorage Create(IServiceProvider services, string name)
        {
            var referenceExtractorGrainStateTypeInfoProvider = new ReferenceExtractorGrainStateTypeInfoProvider(
                services.GetRequiredService<IGrainReferenceExtractor>());

            // pass in custom grain state type info provider, which will do the reference extraction for grain-reference type
            var cosmosGrainStorage = (CosmosGrainStorage)CosmosStorageFactory.Create(services, name, referenceExtractorGrainStateTypeInfoProvider);

            return new MigrationAzureCosmosGrainStorage(
                name,
                cosmosGrainStorage,
                referenceExtractorGrainStateTypeInfoProvider,
                services.GetRequiredService<ILoggerFactory>().CreateLogger<MigrationAzureCosmosGrainStorage>());
        }
    }
}
