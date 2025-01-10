using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Persistence.Cosmos.Migration
{
    /// <summary>
    /// Simple storage provider for writing grain state data to Azure blob storage in JSON format.
    /// </summary>
    public class MigrationAzureCosmosGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
    {
        public Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState) => throw new NotImplementedException();
        public IAsyncEnumerable<StorageEntry> GetAll(CancellationToken cancellationToken) => throw new NotImplementedException();
        public void Participate(ISiloLifecycle lifecycle) => throw new NotImplementedException();
        public Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState) => throw new NotImplementedException();
        public Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState) => throw new NotImplementedException();
    }

    public static class MigrationAzureCosmosGrainStorageFactory
    {
        public static IGrainStorage Create(IServiceProvider services, string name)
        {
            var optionsMonitor = services.GetRequiredService<IOptionsMonitor<CosmosOptions>>();
            return ActivatorUtilities.CreateInstance<MigrationAzureCosmosGrainStorage>(services, name, optionsMonitor.Get(name));
        }
    }
}
