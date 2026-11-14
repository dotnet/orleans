using System;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Orleans.Runtime;
using Orleans.Dashboard.Implementation;
using Microsoft.Extensions.Hosting;
using Orleans.Dashboard.Core;

namespace Orleans.Dashboard;

internal sealed partial class DashboardHost(
    ILogger<DashboardHost> logger,
    ILocalSiloDetails localSiloDetails,
    OrleansInstruments instruments,
    IGrainFactory grainFactory,
    DashboardTelemetryExporter dashboardTelemetryExporter,
    ISiloGrainClient siloGrainClient) : IHostedService, ILifecycleParticipant<ISiloLifecycle>, IAsyncDisposable, IDisposable
{
    private MeterProvider? _meterProvider;

    /// <summary>
    /// Gets the interval between periodic telemetry exports. <see cref="Timeout.Infinite"/> disables periodic export,
    /// so only explicit flushes collect telemetry.
    /// </summary>
    internal int ExportIntervalMilliseconds { get; init; } = 1000;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            ActivateDashboardGrainAsync(cancellationToken),
            ActivateSiloGrainAsync(cancellationToken)).ConfigureAwait(false);
    }

    public void Participate(ISiloLifecycle lifecycle)
    {
        // Observe client connections from the moment the gateway opens at the Active stage.
        lifecycle.Subscribe<DashboardHost>(ServiceLifecycleStage.BecomeActive, _ => StartOpenTelemetryConsumerAsync());
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ActivateSiloGrainAsync(CancellationToken cancellationToken)
    {
        try
        {
            var siloGrain = siloGrainClient.GrainService(localSiloDetails.SiloAddress);
            await siloGrain.SetVersion(GetOrleansVersion(), GetHostVersion(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogWarningActivateSiloGrainServiceStartupFailed(logger, ex);
        }
    }

    private async Task ActivateDashboardGrainAsync(CancellationToken cancellationToken)
    {
        try
        {
            var dashboardGrain = grainFactory.GetGrain<IDashboardGrain>(0);
            await dashboardGrain.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogWarningActivateDashboardGrainStartupFailed(logger, ex);
        }
    }

    private Task StartOpenTelemetryConsumerAsync()
    {
        _meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(instruments.Meter.Name)
            .AddView(instrument =>
            {
                if (!ReferenceEquals(instrument.Meter, instruments.Meter))
                {
                    return MetricStreamConfiguration.Drop;
                }

                // The dashboard displays silo-wide totals, so collect a single stream with histogram sums only.
                return instrument.GetType().IsGenericType && instrument.GetType().GetGenericTypeDefinition() == typeof(Histogram<>)
                    ? new ExplicitBucketHistogramConfiguration { TagKeys = [], Boundaries = [], RecordMinMax = false }
                    : null;
            })
            .AddReader(new PeriodicExportingMetricReader(dashboardTelemetryExporter, ExportIntervalMilliseconds, 1000))
            .Build();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsync(_meterProvider).ConfigureAwait(false);

        static async ValueTask DisposeAsync(object? obj)
        {
            try
            {
                if (obj is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else if (obj is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch
            {
                // Ignore.
            }
        }
    }


    public void Dispose()
    {
        try
        {
            _meterProvider?.Dispose();
        }
        catch
        {
            /* NOOP */
        }
    }

    private static string GetOrleansVersion()
    {
        var assembly = typeof(SiloAddress).GetTypeInfo().Assembly;
        return $"{assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} ({assembly.GetName().Version})";
    }

    private static string GetHostVersion()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly();

            if (assembly != null)
            {
                return assembly.GetName().Version!.ToString();
            }
        }
        catch
        {
            /* NOOP */
        }

        return "1.0.0.0";
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Unable to activate silo grain service during startup. The service will be activated on first use."
    )]
    private static partial void LogWarningActivateSiloGrainServiceStartupFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Unable to activate dashboard grain during startup. The grain will be activated on first use."
    )]
    private static partial void LogWarningActivateDashboardGrainStartupFailed(ILogger logger, Exception exception);
}
