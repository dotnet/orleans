using System;
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

internal sealed class DashboardHost(
    ILogger<DashboardHost> logger,
    ILocalSiloDetails localSiloDetails,
    IGrainFactory grainFactory,
    DashboardTelemetryExporter dashboardTelemetryExporter,
    ISiloGrainClient siloGrainClient) : IHostedService, IAsyncDisposable, IDisposable
{
    private MeterProvider _meterProvider;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            ActivateDashboardGrainAsync(),
            ActivateSiloGrainAsync(),
            StartOpenTelemetryConsumerAsync()).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ActivateSiloGrainAsync()
    {
        try
        {
            var siloGrain = siloGrainClient.GrainService(localSiloDetails.SiloAddress);
            await siloGrain.SetVersion(GetOrleansVersion(), GetHostVersion()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to activate silo grain service during startup. The service will be activated on first use.");
        }
    }

    private async Task ActivateDashboardGrainAsync()
    {
        try
        {
            var dashboardGrain = grainFactory.GetGrain<IDashboardGrain>(0);
            await dashboardGrain.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to activate dashboard grain during startup. The grain will be activated on first use.");
        }
    }

    private Task StartOpenTelemetryConsumerAsync()
    {
        _meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter("Orleans")
            .AddReader(new PeriodicExportingMetricReader(dashboardTelemetryExporter, 1000, 1000))
            .Build();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsync(_meterProvider).ConfigureAwait(false);

        static async ValueTask DisposeAsync(object obj)
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
                return assembly.GetName().Version.ToString();
            }
        }
        catch
        {
            /* NOOP */
        }

        return "1.0.0.0";
    }
}
