using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Dashboard;
using Orleans.Hosting;

namespace Documentation.Dashboard;

internal static class DashboardSnippets
{
    internal static void MapDashboard(WebApplicationBuilder builder)
    {
        // <map_dashboard>
builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering()
        .AddDashboard();
});

var app = builder.Build();

app.MapOrleansDashboard("/dashboard")
    .RequireAuthorization();
        // </map_dashboard>
    }

    internal static void ConfigureDashboard(ISiloBuilder siloBuilder)
    {
        // <configure_dashboard>
siloBuilder.AddDashboard(options =>
{
    options.HideTrace = true;
    options.CounterUpdateIntervalMs = 2_000;
    options.HistoryLength = 100;
});
        // </configure_dashboard>
    }

    internal static void ConfigureProfiler(WebApplicationBuilder builder)
    {
        // <configure_profiler>
builder.Services.Configure<GrainProfilerOptions>(options =>
{
    options.TraceAlways = true;
    options.DeactivationTime = TimeSpan.FromMinutes(5);
});
        // </configure_profiler>
    }

    internal static void ConfigureClient(
        HostApplicationBuilder dashboardBuilder,
        Uri gatewayAddress)
    {
        // <configure_dashboard_client>
dashboardBuilder.UseOrleansClient(clientBuilder =>
{
    clientBuilder
        .UseStaticClustering(options => options.Gateways.Add(gatewayAddress))
        .AddDashboard();
});
        // </configure_dashboard_client>
    }
}
