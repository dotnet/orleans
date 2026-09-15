using Aspire.Hosting;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.Storage;
using DurableJobsJournaling;
using DurableJobsJournaling.AppHost.OpenTelemetryCollector;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var prometheus = builder.AddContainer("prometheus", "prom/prometheus", "v3.2.1")
    .WithBindMount(Path.Combine("monitoring", "prometheus"), "/etc/prometheus", isReadOnly: true)
    .WithArgs("--web.enable-otlp-receiver", "--config.file=/etc/prometheus/prometheus.yml")
    .WithHttpEndpoint(targetPort: 9090, name: "http")
    .WithUrlForEndpoint("http", url => url.DisplayText = "Prometheus");

var grafana = builder.AddContainer("grafana", "grafana/grafana", "12.3.0")
    .WithBindMount(Path.Combine("monitoring", "grafana", "config", "grafana.ini"), "/etc/grafana/grafana.ini", isReadOnly: true)
    .WithBindMount(Path.Combine("monitoring", "grafana", "config", "provisioning"), "/etc/grafana/provisioning", isReadOnly: true)
    .WithBindMount(Path.Combine("monitoring", "grafana", "dashboards"), "/var/lib/grafana/dashboards", isReadOnly: true)
    .WithEnvironment("PROMETHEUS_ENDPOINT", prometheus.GetEndpoint("http"))
    .WithHttpEndpoint(targetPort: 3000, name: "http")
    .WithUrlForEndpoint("http", url => url.DisplayText = "Grafana")
    .WaitFor(prometheus);

var otelCollector = builder.AddOpenTelemetryCollector("otelcollector", Path.Combine("monitoring", "otelcollector", "config.yaml"))
    .WithEnvironment("PROMETHEUS_ENDPOINT", $"{prometheus.GetEndpoint("http")}/api/v1/otlp")
    .WaitFor(prometheus);

const string OtelMetricExportIntervalMilliseconds = "5000";

var backend = StorageBackendConfiguration.Parse(builder.Configuration.GetValue("Playground:Storage:Provider", "Azurite"));

var storage = builder.AddAzureStorage("storage");
if (backend.IsEmulator())
{
    storage.RunAsEmulator();
}
else
{
    storage.ConfigureInfrastructure(infrastructure =>
    {
        var storageAccount = infrastructure.GetProvisionableResources().OfType<StorageAccount>().Single();
        storageAccount.Kind = backend == StorageBackend.PremiumBlob ? StorageKind.BlockBlobStorage : StorageKind.StorageV2;
        storageAccount.Sku = new StorageSku { Name = backend == StorageBackend.PremiumBlob ? StorageSkuName.PremiumLrs : StorageSkuName.StandardLrs };
        if (backend == StorageBackend.PremiumBlob)
        {
            storageAccount.AccessTier.ClearValue();
            RemoveUnsupportedPremiumBlobStorageOutputs(infrastructure);
        }
    });
}
var tableStorage = backend == StorageBackend.PremiumBlob ? builder.AddAzureStorage("clusteringstorage") : storage;
if (backend == StorageBackend.PremiumBlob)
{
    tableStorage.ConfigureInfrastructure(infrastructure =>
    {
        var storageAccount = infrastructure.GetProvisionableResources().OfType<StorageAccount>().Single();
        storageAccount.Sku = new StorageSku { Name = StorageSkuName.StandardLrs };
    });
}

if (!backend.IsEmulator() && builder.ExecutionContext.IsPublishMode)
{
    storage.ClearDefaultRoleAssignments();
    tableStorage.ClearDefaultRoleAssignments();
}

var tables = tableStorage.AddTables("tables");

var orleans = builder.AddOrleans("cluster")
    .WithClustering(tables);

var runId = Guid.NewGuid().ToString("N");
var silo = builder.AddProject<Projects.DurableJobsJournaling_Silo>("silo")
    .WithReference(orleans)
    .WithReference(tables)
    .WaitFor(tables)
    .WaitFor(otelCollector)
    .WithReplicas(1)
    .WithEnvironment("Playground__Storage__Provider", backend.ToString())
    .WithEnvironment("Playground__Storage__Container", $"durablejobs-{runId}")
    .WithEnvironment("Playground__Storage__Table", $"durablejobs{runId}")
    .WithEnvironment("OTEL_METRIC_EXPORT_INTERVAL", OtelMetricExportIntervalMilliseconds);

if (backend.UsesTableJournal())
{
    var journals = storage.AddTables("journals");
    silo.WithReference(journals).WaitFor(journals);
}
else
{
    var blobs = storage.AddBlobs("blobs");
    silo.WithReference(blobs).WaitFor(blobs);
}

builder.AddProject<Projects.DurableJobsJournaling_Web>("web")
    .WithReference(orleans.AsClient())
    .WithReference(tables)
    .WithEnvironment("GRAFANA_URL", grafana.GetEndpoint("http"))
    .WithEnvironment("OTEL_METRIC_EXPORT_INTERVAL", OtelMetricExportIntervalMilliseconds)
    .WaitFor(silo)
    .WaitFor(tables)
    .WaitFor(otelCollector)
    .WithReplicas(1);

builder.Build().Run();

static void RemoveUnsupportedPremiumBlobStorageOutputs(AzureResourceInfrastructure infrastructure)
{
    foreach (var output in infrastructure.GetProvisionableResources()
        .OfType<ProvisioningOutput>()
        .Where(output => output.BicepIdentifier is "queueEndpoint" or "tableEndpoint")
        .ToArray())
    {
        infrastructure.Remove(output);
    }
}
