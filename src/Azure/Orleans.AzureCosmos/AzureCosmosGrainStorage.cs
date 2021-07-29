using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Orleans.AzureCosmos
{
    internal sealed class AzureCosmosGrainStorage : AzureCosmosStorage, IGrainStorage, ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver
    {
        private readonly AzureCosmosStorageOptions options;
        private readonly string partitionPrefix;
        private readonly IServiceProvider services;
        private readonly string name;
        private JsonSerializer serializer;

        public static IGrainStorage Create(IServiceProvider sp, string name)
            => ActivatorUtilities.CreateInstance<AzureCosmosGrainStorage>(sp, name, sp.GetProviderClusterOptions(name));

        public AzureCosmosGrainStorage(
            string name,
            IOptionsMonitor<AzureCosmosStorageOptions> optionsSnapshot,
            IOptions<ClusterOptions> clusterOptions,
            IServiceProvider services,
            ILoggerFactory loggerFactory)
            : base(loggerFactory)
        {
            this.options = optionsSnapshot.Get(name);
            this.partitionPrefix = clusterOptions.Value.ServiceId + "/";
            this.name = name;
            this.services = services;
        }

        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            try
            {
                if (container == null) throw new InvalidOperationException("Uninitialized");

                var pk = GetKeyString(grainReference);
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Reading: GrainType={0} PK={1} GrainId={2} from Container={3}", grainType, pk, grainReference, options.ContainerName);

                var startTime = DateTime.UtcNow;
                // Task.Run is a workaround for https://github.com/Azure/azure-cosmos-dotnet-v2/issues/687
                using var res = await Task.Run(() => container.ReadItemStreamAsync(grainType, new PartitionKey(pk)));
                CheckAlertSlowAccess(startTime, "ReadItem");

                if (res.StatusCode == HttpStatusCode.NotFound)
                {
                    if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("NotFound reading: PartitionKey={0} RowKey={1} from Container={2}", pk, grainType, options.ContainerName);
                    return;
                }

                var eTag = res.EnsureSuccessStatusCode().Headers.ETag;
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Read: PartitionKey={0} RowKey={1} from Container={2} with ETag={3}", pk, grainType, options.ContainerName, eTag);

                var record = (GrainStateRecord)serializer.Deserialize(new StreamReader(res.Content), typeof(GrainStateRecord<>).MakeGenericType(grainState.Type));
                grainState.State = record.Entity;
                grainState.ETag = eTag;
                grainState.RecordExists = true;
            }
            catch (Exception ex) when (Log(ex)) { throw; }
        }

        public async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            try
            {
                if (container == null) throw new InvalidOperationException("Uninitialized");

                var pk = GetKeyString(grainReference);
                var eTag = grainState.ETag;
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Writing: GrainType={0} PK={1} GrainId={2} ETag={3} to Container={4}", grainType, pk, grainReference, eTag, options.ContainerName);

                var record = (GrainStateRecord)Activator.CreateInstance(typeof(GrainStateRecord<>).MakeGenericType(grainState.Type));
                record.GrainReference = pk;
                record.Id = grainType;
                record.Entity = grainState.State;

                var ms = new MemoryStream();
                var writer = new StreamWriter(ms);
                serializer.Serialize(writer, record);
                writer.Flush();

                var overwrite = false;
retry:
                var operation = overwrite ? "UpsertItem" : eTag is null ? "CreateItem" : "ReplaceItem";
                var payload = new MemoryStream(ms.GetBuffer(), 0, (int)ms.Length, false, true);
                var pkr = new PartitionKey(pk);
                var startTime = DateTime.UtcNow;
                // Task.Run is a workaround for https://github.com/Azure/azure-cosmos-dotnet-v2/issues/687
                var task = Task.Run(() => overwrite ? container.UpsertItemStreamAsync(payload, pkr, noContentResponse)
                    : eTag is null ? container.CreateItemStreamAsync(payload, pkr, noContentResponse)
                    : container.ReplaceItemStreamAsync(payload, grainType, pkr, requestOptions: new() { IfMatchEtag = eTag, EnableContentResponseOnWrite = false }));
                using (var res = await task)
                {
                    CheckAlertSlowAccess(startTime, operation);

                    if (UpdateConflict(res, operation, grainType, grainReference, eTag, out var inconsistentState))
                    {
                        if (options.OverwriteStateOnUpdateConflict && !overwrite)
                        {
                            overwrite = true;
                            goto retry;
                        }
                        throw inconsistentState;
                    }

                    eTag = res.EnsureSuccessStatusCode().Headers.ETag;
                    grainState.ETag = eTag;
                    grainState.RecordExists = true;
                    if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Wrote: PartitionKey={0} RowKey={1} to Container={2} with ETag={3}", pk, grainType, options.ContainerName, eTag);
                }
            }
            catch (Exception ex) when (Log(ex)) { throw; }
        }

        public async Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            try
            {
                if (container == null) throw new InvalidOperationException("Uninitialized");

                var eTag = grainState.ETag;
                if (eTag is null)
                {
                    if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Not attempting to delete non-existent persistent state: PartitionKey={0} RowKey={1} from Container={2}", GetKeyString(grainReference), grainType, options.ContainerName);
                    return;
                }

                string pk = GetKeyString(grainReference);
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Deleting: GrainType={0} PK={1} GrainId={2} ETag={3} from Container={4}", grainType, pk, grainReference, eTag, options.ContainerName);

                var overwrite = false;
retry:
                var ro = overwrite ? null : new ItemRequestOptions { IfMatchEtag = eTag };
                var startTime = DateTime.UtcNow;
                // Task.Run is a workaround for https://github.com/Azure/azure-cosmos-dotnet-v2/issues/687
                using (var res = await Task.Run(() => container.DeleteItemStreamAsync(grainType, new PartitionKey(pk), ro)))
                {
                    CheckAlertSlowAccess(startTime, "DeleteItem");

                    if (res.StatusCode == HttpStatusCode.NotFound)
                    {
                        grainState.ETag = null;
                        grainState.RecordExists = false;
                        logger.Info("State was not found when deleting: PartitionKey={0} RowKey={1} from Container={2} with ETag={3}", pk, grainType, options.ContainerName, eTag);
                        return;
                    }

                    if (UpdateConflict(res, "DeleteItem", grainType, grainReference, eTag, out var inconsistentState))
                    {
                        if (options.OverwriteStateOnUpdateConflict && !overwrite)
                        {
                            overwrite = true;
                            goto retry;
                        }
                        throw inconsistentState;
                    }

                    res.EnsureSuccessStatusCode();
                    grainState.ETag = null;
                    grainState.RecordExists = false;
                    if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Deleted: PartitionKey={0} RowKey={1} from Container={2}", pk, grainType, options.ContainerName);
                }
            }
            catch (Exception ex) when (Log(ex)) { throw; }
        }

        private bool UpdateConflict(ResponseMessage res, string operation, string grainType, GrainReference grainReference, string eTag, out InconsistentStateException ex)
        {
            var code = res.StatusCode;
            if (code is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
            {
                logger.LogWarning($"{operation} error {code}: GrainType={grainType} GrainId={grainReference} Container={options.ContainerName} CurrentETag={eTag}");
                ex = new($"Grain state conflict. GrainType: {grainType}, GrainId: {grainReference}, ContainerName: {options.ContainerName}, CurrentETag: {eTag}", "Unknown", eTag);
                return true;
            }
            ex = null;
            return false;
        }

        private string GetKeyString(GrainReference grainReference) => partitionPrefix + grainReference.ToKeyString();

        private abstract class GrainStateRecord
        {
            [JsonProperty("id", Required = Required.Always)]
            public string Id { get; set; }

            [JsonProperty(nameof(GrainReference), Required = Required.Always)]
            public string GrainReference { get; set; }

            internal object Entity { get; set; }
        }

        private sealed class GrainStateRecord<T> : GrainStateRecord
        {
            public new T Entity
            {
                get => (T)base.Entity;
                set => base.Entity = value;
            }
        }

        public Task OnStart(CancellationToken ct) => Task.Run(() =>
        {
            try
            {
                logger.Info("Initializing {0} grain state container for service id {1}", name, partitionPrefix);
                var settings = OrleansJsonSerializer.GetDefaultSerializerSettings(services);
                // set some more sane defaults
                settings.DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate;
#pragma warning disable CA2327 // grain state is a trusted input (generated by the same application)
                settings.TypeNameHandling = TypeNameHandling.Auto;
                options.ConfigureJsonSerializerSettings?.Invoke(settings);
                serializer = JsonSerializer.Create(settings);
                return Init(options, new()
                {
                    PartitionKeyPath = "/" + nameof(GrainStateRecord.GrainReference),
                    // minimal indexing for TTL support only
                    IndexingPolicy = new() { ExcludedPaths = { new() { Path = "/*" } } }
                });
            }
            catch (Exception ex) when (Log(ex)) { throw; }
        });

        public Task OnStop(CancellationToken ct)
        {
            container = null;
            return Task.CompletedTask;
        }

        public void Participate(ISiloLifecycle lifecycle) => lifecycle.Subscribe(OptionFormattingUtilities.Name<AzureCosmosGrainStorage>(name), options.InitStage, this);
    }
}
