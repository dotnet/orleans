using AuthenticatedSiloConnections;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var options = SampleOptions.Load(builder.Configuration);
var exportToOtlp = !string.IsNullOrWhiteSpace(
    builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

// <FixedDiagnostics>
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(console =>
{
    console.TimestampFormat = "O";
    console.JsonWriterOptions = new() { Indented = false };
});
builder.Logging.AddFilter("Orleans.Connections.Security", LogLevel.Information);
builder.Logging.AddFilter("Azure.Identity", LogLevel.Warning);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: "authenticated-orleans-silo",
        serviceInstanceId: Environment.MachineName))
    .WithMetrics(metrics =>
    {
        metrics.AddMeter("Microsoft.Orleans.Connections.Security");

        if (exportToOtlp)
        {
            metrics.AddOtlpExporter();
        }
    });
// </FixedDiagnostics>

// <ExplicitCredential>
TokenCredential credential = new WorkloadIdentityCredential(
    new WorkloadIdentityCredentialOptions
    {
        TenantId = options.Entra.TenantId,
        ClientId = options.Entra.WorkloadClientId,
        TokenFilePath = options.Entra.FederatedTokenFile,
    });
// </ExplicitCredential>

using var siloCertificate = CertificatePolicy.LoadSiloCertificate(
    options.Certificate.Path,
    options.Certificate.Password);

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.UseLocalhostClustering(
        siloPort: options.SiloPort,
        gatewayPort: options.GatewayPort,
        primarySiloEndpoint: options.PrimarySiloEndpoint,
        serviceId: options.ServiceId,
        clusterId: options.ClusterId);

    SiloAuthentication.Configure(
        siloBuilder,
        options,
        credential,
        siloCertificate);
});

await builder.Build().RunAsync();
