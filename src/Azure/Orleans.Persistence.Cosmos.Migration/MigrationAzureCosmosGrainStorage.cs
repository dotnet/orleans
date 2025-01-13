using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    /// Simple storage provider for writing grain state data to Azure blob storage in JSON format.
    /// </summary>
    public class MigrationAzureCosmosGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
    {
        private ILogger logger;
        private readonly string name;
        private CosmosGrainStorageOptions options;

        private readonly IGrainReferenceExtractor grainReferenceExtractor;
        private IGrainStorageSerializer grainStorageSerializer;
        private readonly IServiceProvider services;

        // private BlobContainerClient container;

        public MigrationAzureCosmosGrainStorage(
            string name,
            CosmosGrainStorageOptions options,
            IGrainStorageSerializer grainStorageSerializer,
            IGrainReferenceExtractor grainReferenceExtractor,
            IServiceProvider services,
            ILogger<MigrationAzureCosmosGrainStorage> logger)
        {
            this.name = name;
            this.options = options;
            this.grainStorageSerializer = grainStorageSerializer;
            this.grainReferenceExtractor = grainReferenceExtractor;
            this.services = services;
            this.logger = logger;
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(OptionFormattingUtilities.Name<MigrationAzureCosmosGrainStorage>(this.name), this.options.InitStage, Init);
        }

        public Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState) => throw new NotImplementedException();
        public IAsyncEnumerable<StorageEntry> GetAll(CancellationToken cancellationToken) => throw new NotImplementedException();
        

        public Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState) => throw new NotImplementedException();
        public Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState) => throw new NotImplementedException();

        /// <summary> Initialization function for this storage provider. </summary>
        private async Task Init(CancellationToken ct)
        {
            await Task.Delay(1);
        }
    }

    public static class MigrationAzureCosmosGrainStorageFactory
    {
        public static IGrainStorage Create(IServiceProvider services, string name)
        {
            var optionsMonitor = services.GetRequiredService<IOptionsMonitor<CosmosGrainStorageOptions>>();
            return ActivatorUtilities.CreateInstance<MigrationAzureCosmosGrainStorage>(services, name, optionsMonitor.Get(name));
        }
    }
}
