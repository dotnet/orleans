using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        private CosmosGrainStorageOptions options;

        private readonly IGrainReferenceExtractor grainReferenceExtractor;
        private IGrainStorageSerializer grainStorageSerializer;

        private readonly CosmosGrainStorage cosmosGrainStorage;

        public MigrationAzureCosmosGrainStorage(
            string name,
            CosmosGrainStorage cosmosGrainStorage,
            CosmosGrainStorageOptions options,
            IGrainStorageSerializer grainStorageSerializer,
            IGrainReferenceExtractor grainReferenceExtractor,
            ILogger<MigrationAzureCosmosGrainStorage> logger)
        {
            this.name = name;
            this.cosmosGrainStorage = cosmosGrainStorage;
            this.options = options;
            this.grainStorageSerializer = grainStorageSerializer;
            this.grainReferenceExtractor = grainReferenceExtractor;
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
            var grainTypeData = GetGrainStateTypeInfo(grainReference, grainState);
            return cosmosGrainStorage.WriteStateAsync(grainTypeData, stateName, grainReference, grainState);
        }

        private GrainStateTypeInfo GetGrainStateTypeInfo(GrainReference grainReference, IGrainState grainState)
        {
            // grainState.Type does not have a proper type -> we need to separately call extractor to find out a proper type
            var grainClass = grainReferenceExtractor.ExtractType(grainReference);

            var grainTypeAttr = grainClass.GetCustomAttribute<GrainTypeAttribute>();
            if (grainTypeAttr is null)
            {
                throw new InvalidOperationException($"All grain classes must specify a grain type name using the [GrainType(type)] attribute. Grain class '{grainClass}' does not.");
            }
            var grainTypeName = grainTypeAttr.GrainType;
            var grainStateType = grainState.Type;
            var grainKeyFormatter = GrainStateTypeInfo.GetGrainKeyFormatter(grainClass);

            var readStateFunc = CosmosGrainStorage.ReadStateAsyncCoreMethodInfo.MakeGenericMethod(grainStateType).CreateDelegate<Func<string, GrainId, IGrainState, Task>>(this.cosmosGrainStorage);
            var writeStateFunc = CosmosGrainStorage.WriteStateAsyncCoreMethodInfo.MakeGenericMethod(grainStateType).CreateDelegate<Func<string, GrainId, IGrainState, Task>>(this.cosmosGrainStorage);
            var clearStateFunc = CosmosGrainStorage.ClearStateAsyncCoreMethodInfo.MakeGenericMethod(grainStateType).CreateDelegate<Func<string, GrainId, IGrainState, Task>>(this.cosmosGrainStorage);

            return new GrainStateTypeInfo(
                grainTypeName,
                grainKeyFormatter,
                readStateFunc,
                writeStateFunc,
                clearStateFunc);
        }
    }

    public static class MigrationAzureCosmosGrainStorageFactory
    {
        public static IMigrationGrainStorage Create(IServiceProvider services, string name)
        {
            var cosmosGrainStorage = (CosmosGrainStorage)CosmosStorageFactory.Create(services, name);
            var optionsMonitor = services.GetRequiredService<IOptionsMonitor<CosmosGrainStorageOptions>>();

            return new MigrationAzureCosmosGrainStorage(
                name,
                cosmosGrainStorage,
                optionsMonitor.Get(name),
                services.GetRequiredService<IGrainStorageSerializer>(),
                services.GetRequiredService<IGrainReferenceExtractor>(),
                services.GetRequiredService<ILoggerFactory>().CreateLogger<MigrationAzureCosmosGrainStorage>());
        }
    }
}
