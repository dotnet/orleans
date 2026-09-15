using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var exportToOtlp = !string.IsNullOrWhiteSpace(
    builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

// <OpenTelemetry>
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;

    if (exportToOtlp)
    {
        logging.AddOtlpExporter();
    }
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(
            serviceName: "orders-silo",
            serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString(),
            serviceInstanceId: Environment.MachineName)
        .AddAttributes([
            new("deployment.environment.name", builder.Environment.EnvironmentName),
        ]))
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter("Microsoft.Orleans")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation();

        if (exportToOtlp)
        {
            metrics.AddOtlpExporter();
        }
    })
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(
                "Microsoft.Orleans.Application",
                "Microsoft.Orleans.Runtime",
                "Microsoft.Orleans.Lifecycle",
                "Microsoft.Orleans.Storage",
                "Microsoft.Orleans.Reminders")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .SetSampler(new ParentBasedSampler(
                new TraceIdRatioBasedSampler(0.1)));

        if (exportToOtlp)
        {
            tracing.AddOtlpExporter();
        }
    });

builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering()
        .AddActivityPropagation();
});
// </OpenTelemetry>

var app = builder.Build();
await app.RunAsync();
