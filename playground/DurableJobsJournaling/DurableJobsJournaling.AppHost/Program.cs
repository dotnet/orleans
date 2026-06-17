using Aspire.Hosting;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.Storage;
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

var storageProvider = builder.Configuration.GetValue("Playground:Storage:Provider", "Azurite");
var useAzurite = storageProvider.Equals("Azurite", StringComparison.OrdinalIgnoreCase);
var useAzure = storageProvider.Equals("Azure", StringComparison.OrdinalIgnoreCase);

var storage = builder.AddAzureStorage("storage");
if (useAzurite)
{
    storage.RunAsEmulator();
}
else if (useAzure)
{
    storage.ConfigureInfrastructure(infrastructure =>
    {
        var storageAccount = infrastructure.GetProvisionableResources().OfType<StorageAccount>().Single();
        storageAccount.Kind = StorageKind.BlockBlobStorage;
        storageAccount.Sku = new StorageSku { Name = StorageSkuName.PremiumLrs };
        storageAccount.AccessTier.ClearValue();
        RemoveUnsupportedPremiumBlobStorageOutputs(infrastructure);
    });
}
else
{
    throw new InvalidOperationException($"Unknown Playground:Storage:Provider value '{storageProvider}'. Use 'Azurite' or 'Azure'.");
}

var blobs = storage.AddBlobs("blobs");
var tableStorage = useAzurite ? storage : builder.AddAzureStorage("clusteringstorage");
if (useAzure && builder.ExecutionContext.IsPublishMode)
{
    storage.ClearDefaultRoleAssignments();
    tableStorage.ClearDefaultRoleAssignments();
}

var tables = tableStorage.AddTables("tables");

var orleans = builder.AddOrleans("cluster")
    .WithClustering(tables);

var storagePrefix = $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
var silo = builder.AddProject<Projects.DurableJobsJournaling_Silo>("silo")
    .WithReference(orleans)
    .WithReference(blobs)
    .WithReference(tables)
    .WaitFor(blobs)
    .WaitFor(tables)
    .WaitFor(otelCollector)
    .WithReplicas(1)
    .WithEnvironment("Playground__Storage__Container", "durablejobs-journaling-playground")
    .WithEnvironment("Playground__Storage__Prefix", storagePrefix)
    .WithEnvironment("OTEL_METRIC_EXPORT_INTERVAL", OtelMetricExportIntervalMilliseconds);

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
