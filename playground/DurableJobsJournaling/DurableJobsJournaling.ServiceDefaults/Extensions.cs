using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private const string PlaygroundMeterName = "Orleans.Playground.DurableJobsJournaling";
    private static readonly double[] LatencyHistogramBoundaries =
    [
        0, 1, 2, 5, 10, 25, 50, 100, 250, 500,
        1_000, 2_000, 5_000, 10_000, 15_000, 20_000, 30_000, 60_000
    ];

    private static readonly double[] BatchSizeHistogramBoundaries =
    [
        1, 2, 4, 8, 16, 32, 64, 128,
        256, 512, 1_024, 2_048, 4_096, 8_192, 16_384
    ];

    private static readonly double[] StorageOperationByteHistogramBoundaries =
    [
        256, 512, 1_024, 2_048, 4_096, 8_192, 16_384, 32_768,
        65_536, 131_072, 262_144, 524_288, 1_048_576, 2_097_152,
        4_194_304, 8_388_608, 16_777_216, 33_554_432, 67_108_864, 104_857_600
    ];

    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("Microsoft.Orleans")
                    .AddMeter(PlaygroundMeterName)
                    .AddView("playground.grain.request.duration", CreateLatencyHistogramConfiguration())
                    .AddView("playground.workflow.duration", CreateLatencyHistogramConfiguration())
                    .AddView("playground.stage.schedule_to_start", CreateLatencyHistogramConfiguration())
                    .AddView("playground.stage.handoff", CreateLatencyHistogramConfiguration())
                    .AddView("playground.stage.state_write", CreateLatencyHistogramConfiguration())
                    .AddView("playground.stage.schedule_call", CreateLatencyHistogramConfiguration())
                    .AddView("playground.stage.due_wait", CreateLatencyHistogramConfiguration())
                    .AddView("playground.stage.queue_delay", CreateLatencyHistogramConfiguration())
                    .AddView("playground.stage.execution", CreateLatencyHistogramConfiguration())
                    .AddView("playground.stage.total", CreateLatencyHistogramConfiguration())
                    .AddView("playground.stage.workflow_elapsed", CreateLatencyHistogramConfiguration())
                    .AddView("playground.stage.unaccounted", CreateLatencyHistogramConfiguration())
                    .AddView("playground.stage.reschedule_gap", CreateLatencyHistogramConfiguration())
                    .AddView("orleans-durablejobs-job-dispatch-lag", CreateLatencyHistogramConfiguration())
                    .AddView("orleans-durablejobs-job-attempt-duration", CreateLatencyHistogramConfiguration())
                    .AddView("orleans-durablejobs-handler-execution-duration", CreateLatencyHistogramConfiguration())
                    .AddView("orleans-durablejobs-schedule-job-call-duration", CreateLatencyHistogramConfiguration())
                    .AddView("orleans-journaling-state-write-duration", CreateLatencyHistogramConfiguration())
                    .AddView("orleans-journaling-storage-operation-duration", CreateLatencyHistogramConfiguration())
                    .AddView("orleans-journaling-storage-operation-queue-duration", CreateLatencyHistogramConfiguration())
                    .AddView("orleans-journaling-storage-operation-bytes", CreateStorageOperationByteHistogramConfiguration())
                    .AddView("orleans-journaling-azure-blob-operation-duration", CreateLatencyHistogramConfiguration())
                    .AddView("orleans-durablejobs-storage-batch-size", CreateBatchSizeHistogramConfiguration());
            })
            .WithTracing(tracing =>
            {
                if (builder.Environment.IsDevelopment())
                {
                    tracing.SetSampler(new AlwaysOnSampler());
                }

                tracing
                    .AddSource(builder.Environment.ApplicationName)
                    .AddSource("Microsoft.Orleans.Application")
                    .AddSource("Microsoft.Orleans.Lifecycle")
                    .AddSource("Microsoft.Orleans.Runtime")
                    .AddSource("Microsoft.Orleans.Storage")
                    .AddSource("Microsoft.Orleans.DurableJobs")
                    .AddSource("Azure.*")
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath);
                    })
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static IHostApplicationBuilder AddOpenTelemetryExporters(this IHostApplicationBuilder builder)
    {
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    private static ExplicitBucketHistogramConfiguration CreateLatencyHistogramConfiguration()
        => new() { Boundaries = LatencyHistogramBoundaries };

    private static ExplicitBucketHistogramConfiguration CreateBatchSizeHistogramConfiguration()
        => new() { Boundaries = BatchSizeHistogramBoundaries };

    private static ExplicitBucketHistogramConfiguration CreateStorageOperationByteHistogramConfiguration()
        => new() { Boundaries = StorageOperationByteHistogramBoundaries };

    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapHealthChecks(HealthEndpointPath);
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = healthCheck => healthCheck.Tags.Contains("live")
            });
        }

        return app;
    }
}
