using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// <OpenTelemetry>
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
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
    .WithMetrics(metrics => metrics
        .AddMeter("Microsoft.Orleans")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation())
    .WithTracing(tracing => tracing
        .AddSource(
            "Microsoft.Orleans.Application",
            "Microsoft.Orleans.Runtime",
            "Microsoft.Orleans.Lifecycle",
            "Microsoft.Orleans.Storage")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .SetSampler(new ParentBasedSampler(
            new TraceIdRatioBasedSampler(0.1))));

if (!string.IsNullOrWhiteSpace(
    builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
}

builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering()
        .AddActivityPropagation();
});
// </OpenTelemetry>

var app = builder.Build();
await app.RunAsync();
