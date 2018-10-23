using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.AzureStorage
{
    public class AzureTableTransactionalStateStorageFactory : ITransactionalStateStorageFactory, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly string name;
        private readonly AzureTableTransactionalStateOptions options;
        private readonly ClusterOptions clusterOptions;
        private readonly JsonSerializerSettings jsonSettings;
        private readonly ILoggerFactory loggerFactory;
        private CloudTable table;

        public static ITransactionalStateStorageFactory Create(IServiceProvider services, string name)
        {
            IOptionsSnapshot<AzureTableTransactionalStateOptions> optionsSnapshot = services.GetRequiredService<IOptionsSnapshot<AzureTableTransactionalStateOptions>>();
            return ActivatorUtilities.CreateInstance<AzureTableTransactionalStateStorageFactory>(services, name, optionsSnapshot.Get(name));
        }

        public AzureTableTransactionalStateStorageFactory(string name, AzureTableTransactionalStateOptions options, IOptions<ClusterOptions> clusterOptions, ITypeResolver typeResolver, IGrainFactory grainFactory, ILoggerFactory loggerFactory)
        {
            this.name = name;
            this.options = options;
            this.clusterOptions = clusterOptions.Value;
            this.jsonSettings = TransactionalStateFactory.GetJsonSerializerSettings(
                typeResolver,
                grainFactory);
            this.loggerFactory = loggerFactory;
        }

        public ITransactionalStateStorage<TState> Create<TState>(string stateName, IGrainActivationContext context) where TState : class, new()
        {
            string partitionKey = MakePartitionKey(context, stateName);
            return ActivatorUtilities.CreateInstance<AzureTableTransactionalStateStorage<TState>>(context.ActivationServices, this.table, partitionKey, this.jsonSettings);
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(OptionFormattingUtilities.Name<AzureTableTransactionalStateStorageFactory>(this.name), this.options.InitStage, Init);
        }

        private string MakePartitionKey(IGrainActivationContext context, string stateName)
        {
            string grainKey = context.GrainInstance.GrainReference.ToShortKeyString();
            var key = $"{grainKey}_{this.clusterOptions.ServiceId}_{stateName}";
            return AzureStorageUtils.SanitizeTableProperty(key);
        }

        private async Task CreateTable()
        {
            var tableManager = new AzureTableDataManager<TableEntity>(this.options.TableName, this.options.ConnectionString, this.loggerFactory);
            await tableManager.InitTableAsync().ConfigureAwait(false);
            this.table = tableManager.Table;
        }

        private Task Init(CancellationToken cancellationToken)
        {
            return CreateTable();
        }
    }
}
