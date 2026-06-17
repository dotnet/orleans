using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DurableJobsJournaling.AppHost.OpenTelemetryCollector;

public static class OpenTelemetryCollectorResourceBuilderExtensions
{
    private const string OtelExporterOtlpEndpoint = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string AspireDashboardOtlpUrlVariableName = "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL";
    private const string DotNetDashboardOtlpUrlVariableName = "DOTNET_DASHBOARD_OTLP_ENDPOINT_URL";
    private const string DashboardOtlpApiKeyVariableName = "AppHost:OtlpApiKey";
    private const string DashboardOtlpUrlDefaultValue = "http://localhost:18889";
    private const string OTelCollectorImageName = "ghcr.io/open-telemetry/opentelemetry-collector-releases/opentelemetry-collector-contrib";
    private const string OTelCollectorImageTag = "0.123.0";

    public static IResourceBuilder<OpenTelemetryCollectorResource> AddOpenTelemetryCollector(
        this IDistributedApplicationBuilder builder,
        string name,
        string configFileLocation)
    {
        var url = builder.Configuration[AspireDashboardOtlpUrlVariableName]
            ?? builder.Configuration[DotNetDashboardOtlpUrlVariableName]
            ?? DashboardOtlpUrlDefaultValue;
        var isHttpsEnabled = url.StartsWith("https", StringComparison.OrdinalIgnoreCase);
        var dashboardOtlpEndpoint = new HostUrl(url);

        var collectorResource = new OpenTelemetryCollectorResource(name);
        var resourceBuilder = builder.AddResource(collectorResource)
            .WithImage(OTelCollectorImageName, OTelCollectorImageTag)
            .WithEndpoint(targetPort: 4317, name: OpenTelemetryCollectorResource.OtlpGrpcEndpointName, scheme: "http")
            .WithEndpoint(targetPort: 4318, name: OpenTelemetryCollectorResource.OtlpHttpEndpointName, scheme: "http")
            .WithUrlForEndpoint(OpenTelemetryCollectorResource.OtlpGrpcEndpointName, url => url.DisplayLocation = UrlDisplayLocation.DetailsOnly)
            .WithUrlForEndpoint(OpenTelemetryCollectorResource.OtlpHttpEndpointName, url => url.DisplayLocation = UrlDisplayLocation.DetailsOnly)
            .WithBindMount(configFileLocation, "/etc/otelcol-contrib/config.yaml", isReadOnly: true)
            .WithEnvironment("ASPIRE_ENDPOINT", $"{dashboardOtlpEndpoint}")
            .WithEnvironment("ASPIRE_API_KEY", builder.Configuration[DashboardOtlpApiKeyVariableName] ?? string.Empty)
            .WithEnvironment("ASPIRE_INSECURE", isHttpsEnabled ? "false" : "true")
            .WithArgs("--config=/etc/otelcol-contrib/config.yaml");

        builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            var logger = @event.Services.GetRequiredService<ILogger<OpenTelemetryCollectorResource>>();
            var endpoint = collectorResource.GetEndpoint(OpenTelemetryCollectorResource.OtlpGrpcEndpointName);

            if (!endpoint.Exists)
            {
                logger.LogWarning("No {EndpointName} endpoint for the OpenTelemetry Collector.", OpenTelemetryCollectorResource.OtlpGrpcEndpointName);
                return Task.CompletedTask;
            }

            var appModel = @event.Services.GetRequiredService<DistributedApplicationModel>();
            foreach (var resource in appModel.Resources)
            {
                resource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
                {
                    if (context.EnvironmentVariables.ContainsKey(OtelExporterOtlpEndpoint))
                    {
                        logger.LogDebug("Forwarding telemetry for {ResourceName} to the OpenTelemetry Collector.", resource.Name);
                        context.EnvironmentVariables[OtelExporterOtlpEndpoint] = endpoint;
                    }
                }));
            }

            return Task.CompletedTask;
        });

        return resourceBuilder;
    }
}
