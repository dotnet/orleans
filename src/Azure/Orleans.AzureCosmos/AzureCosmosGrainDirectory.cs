using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.GrainDirectory;
using Orleans.Runtime;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Orleans.AzureCosmos
{
    internal sealed class AzureCosmosGrainDirectory : AzureCosmosStorage, IGrainDirectory, ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver
    {
        private readonly AzureCosmosGrainDirectoryOptions options;
        private readonly string name;
        private readonly string clusterId;
        private readonly PartitionKey partitionKey;

        public static IGrainDirectory Create(IServiceProvider sp, string name)
            => ActivatorUtilities.CreateInstance<AzureCosmosGrainDirectory>(sp, name, sp.GetProviderClusterOptions(name));

        public AzureCosmosGrainDirectory(
            string name,
            IOptionsMonitor<AzureCosmosGrainDirectoryOptions> optionsSnapshot,
            IOptions<ClusterOptions> clusterOptions,
            ILoggerFactory loggerFactory)
            : base(loggerFactory)
        {
            this.name = name;
            this.options = optionsSnapshot.Get(name);
            this.clusterId = clusterOptions.Value.ClusterId;
            this.partitionKey = new(clusterId);
        }

        public void Participate(ISiloLifecycle lifecycle) => lifecycle.Subscribe(OptionFormattingUtilities.Name<AzureCosmosGrainDirectory>(name), ServiceLifecycleStage.RuntimeInitialize, this);

        public Task OnStart(CancellationToken ct) => Task.Run(() =>
        {
            logger.Info("Initializing {0} grain directory container for cluster {1}", name, clusterId);
            return Init(options, new()
            {
                PartitionKeyPath = "/" + nameof(GrainRecord.Cluster),
                IndexingPolicy = new() { ExcludedPaths = { new() { Path = "/*" } } }
            });
        });

        public Task OnStop(CancellationToken ct) => Task.CompletedTask;

        public Task<GrainAddress> Lookup(GrainId grainId) => Lookup(grainId.ToString());

        private async Task<GrainAddress> Lookup(string grainId)
        {
            try
            {
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Reading: GrainId={0} PK={1} from Container={2}", grainId, clusterId, options.ContainerName);

                var startTime = DateTime.UtcNow;
                // Task.Run is a workaround for https://github.com/Azure/azure-cosmos-dotnet-v2/issues/687
                using var res = await Task.Run(() => container.ReadItemStreamAsync(grainId, partitionKey));
                CheckAlertSlowAccess(startTime, "ReadItem");

                if (res.StatusCode == HttpStatusCode.NotFound)
                {
                    if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("NotFound reading: GrainId={0} PK={1} from Container={2}", grainId, clusterId, options.ContainerName);
                    return null;
                }

                res.EnsureSuccessStatusCode();
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Read: GrainId={0} PK={1} from Container={2}", grainId, clusterId, options.ContainerName);
                return Deserialize<GrainRecord>(res).ToGrainAddress();
            }
            catch (Exception ex) when (Log(ex)) { throw; }
        }

        public async Task<GrainAddress> Register(GrainAddress address)
        {
            try
            {
                var record = AsGrainRecord(address);
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Writing: GrainId={0} PK={1} to Container={2}", record.Id, record.Cluster, options.ContainerName);

                var payload = record.Serialize();

                var startTime = DateTime.UtcNow;
                // Task.Run is a workaround for https://github.com/Azure/azure-cosmos-dotnet-v2/issues/687
                using (var res = await Task.Run(() => container.CreateItemStreamAsync(payload, partitionKey, noContentResponse)))
                {
                    CheckAlertSlowAccess(startTime, "CreateItem");
                    if (res.StatusCode != HttpStatusCode.Conflict)
                    {
                        res.EnsureSuccessStatusCode();
                        if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Wrote: GrainId={0} PK={1} to Container={2}", record.Id, record.Cluster, options.ContainerName);
                        return address;
                    }
                }
                logger.Info("Conflict writing: GrainId={0} PK={1} to Container={2}", record.Id, record.Cluster, options.ContainerName);
                return await Lookup(record.Id);
            }
            catch (Exception ex) when (Log(ex)) { throw; }
        }

        public async Task Unregister(GrainAddress address)
        {
            try
            {
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Reading: GrainId={0} PK={1} from Container={2}", address.GrainId, clusterId, options.ContainerName);
                GrainRecord record;

                var startTime = DateTime.UtcNow;
                // Task.Run is a workaround for https://github.com/Azure/azure-cosmos-dotnet-v2/issues/687
                using (var res = await Task.Run(() => container.ReadItemStreamAsync(address.GrainId.ToString(), partitionKey)))
                {
                    CheckAlertSlowAccess(startTime, "ReadItem");

                    if (res.StatusCode == HttpStatusCode.NotFound)
                    {
                        if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("NotFound reading: GrainId={0} PK={1} from Container={2}", address.GrainId, clusterId, options.ContainerName);
                        return;
                    }

                    res.EnsureSuccessStatusCode();
                    if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Read: GrainId={0} PK={1} from Container={2} with ETag={3}", address.GrainId, clusterId, options.ContainerName, res.Headers.ETag);
                    record = Deserialize<GrainRecord>(res);
                }

                if (record.Activation != address.ActivationId)
                {
                    logger.Info("Will not delete: GrainId={0} PK={1} from Container={2} with ActivationId={3} Expected={4}", address.GrainId, clusterId, options.ContainerName, record.Activation, address.ActivationId);
                    return;
                }

                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Deleting: GrainId={0} PK={1} from Container={2}", address.GrainId, clusterId, options.ContainerName);

                startTime = DateTime.UtcNow;
                // Task.Run is a workaround for https://github.com/Azure/azure-cosmos-dotnet-v2/issues/687
                using (var res = await Task.Run(() => container.DeleteItemStreamAsync(record.Id, partitionKey, requestOptions: new() { IfMatchEtag = record.ETag })))
                {
                    CheckAlertSlowAccess(startTime, "DeleteItem");

                    if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
                    {
                        logger.Info("{0} deleting: GrainId={1} PK={2} from Container={3} with ETag={4}", res.StatusCode, address.GrainId, clusterId, options.ContainerName, record.ETag);
                        return;
                    }
                    res.EnsureSuccessStatusCode();
                    if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Deleted: GrainId={0} PK={1} from Container={2}", address.GrainId, clusterId, options.ContainerName);
                }
            }
            catch (Exception ex) when (Log(ex)) { throw; }
        }

        public Task UnregisterSilos(List<SiloAddress> siloAddresses) => Task.CompletedTask;

        private sealed class GrainRecord : RecordBase
        {
            public string Cluster { get; set; }

            public string Activation { get; set; }
            public SiloAddress Address { get; set; }
            public MembershipVersion Version { get; set; }

            public GrainAddress ToGrainAddress() => new()
            {
                GrainId = GrainId.Parse(Id),
                SiloAddress = Address,
                ActivationId = Activation,
                MembershipVersion = Version,
            };
        }

        private GrainRecord AsGrainRecord(GrainAddress r) => new()
        {
            Id = r.GrainId.ToString(),
            Cluster = clusterId,
            Address = r.SiloAddress,
            Activation = r.ActivationId,
            Version = r.MembershipVersion,
        };
    }
}
