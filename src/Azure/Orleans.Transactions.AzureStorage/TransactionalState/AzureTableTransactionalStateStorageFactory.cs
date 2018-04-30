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
using Orleans.Serialization;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.AzureStorage.TransactionalState
{
    public class AzureTableTransactionalStateStorageFactory : ITransactionalStateStorageFactory, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly string name;
        private readonly AzureTableTransactionalStateOptions options;
        private readonly ClusterOptions clusterOptions;
        private readonly ILoggerFactory loggerFactory;
        private CloudTable table;

        public AzureTableTransactionalStateStorageFactory(string name, AzureTableTransactionalStateOptions options, IOptions<ClusterOptions> clusterOptions, ILoggerFactory loggerFactory)
        {
            this.name = name;
            this.options = options;
            this.clusterOptions = clusterOptions.Value;
            this.loggerFactory = loggerFactory;
        }

        public ITransactionalStateStorage<TState> Create<TState>(string stateName, IGrainActivationContext context) where TState : class, new()
        {
            string partitionKey = MakePartitionKey(context, stateName);
            var settingsResolver = ActivatorUtilities.CreateInstance< JsonSerializerSettingsResolver>(context.ActivationServices);
            return ActivatorUtilities.CreateInstance<AzureTableTransactionalStateStorage<TState>>(context.ActivationServices, this.table, partitionKey, settingsResolver.Settings);
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(OptionFormattingUtilities.Name<AzureTableTransactionalStateStorageFactory>(this.name), this.options.InitStage, Init);
        }

        private string MakePartitionKey(IGrainActivationContext context, string stateName)
        {
            string grainKey = context.GrainInstance.GrainReference.ToKeyString();
            var key = $"ts_{this.clusterOptions.ServiceId}_{grainKey}_{stateName}";
            return AzureStorageUtils.SanitizeTableProperty(key);
        }

        private async Task CreateTable()
        {
            var tableManager = new AzureTableDataManager<TableEntity>(this.options.TableName, this.options.ConnectionString, this.loggerFactory);
            await tableManager.InitTableAsync();
            this.table = tableManager.Table;
        }

        private Task Init(CancellationToken cancellationToken)
        {
            return CreateTable();
        }

        private class JsonSerializerSettingsResolver
        {
            public JsonSerializerSettings Settings { get; }
            public JsonSerializerSettingsResolver(ITypeResolver typeResolver, IGrainFactory grainFactory)
            {
                this.Settings = OrleansJsonSerializer.GetDefaultSerializerSettings(typeResolver, grainFactory);
            }
        }

        public static ITransactionalStateStorageFactory Create(IServiceProvider services, string name)
        {
            IOptionsSnapshot<AzureTableTransactionalStateOptions> optionsSnapshot = services.GetRequiredService<IOptionsSnapshot<AzureTableTransactionalStateOptions>>();
            return ActivatorUtilities.CreateInstance<AzureTableTransactionalStateStorageFactory>(services, name, optionsSnapshot.Get(name));
        }
    }
}
