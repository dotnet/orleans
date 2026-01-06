using Orleans.Configuration;
using Orleans.Dashboard;
using System.Net;
using TestGrains;

//
// In this sample we integrate the Dashboard Minimal APIs into the client application.
//
var siloHostBuilder = Host.CreateApplicationBuilder(args);
siloHostBuilder.UseOrleans(builder =>
{
    builder.UseDevelopmentClustering(options => options.PrimarySiloEndpoint = new IPEndPoint(IPAddress.Loopback, 11111));
    builder.UseInMemoryReminderService();
    builder.AddMemoryGrainStorageAsDefault();
    builder.ConfigureEndpoints(IPAddress.Loopback, 11111, 30000);
    builder.AddDashboard();
});
siloHostBuilder.Services.AddSingleton<IHostedService, TestGrainsHostedService>();
using var siloHost = siloHostBuilder.Build();

await siloHost.StartAsync();

await Task.Delay(1000);

// Create a WebApplication for hosting the dashboard
var dashboardBuilder = WebApplication.CreateBuilder(args);

// Configure Orleans client
dashboardBuilder.UseOrleansClient(clientBuilder =>
{
    clientBuilder.UseStaticClustering(options => options.Gateways.Add(new IPEndPoint(IPAddress.Loopback, 30000).ToGatewayUri()));

    // Add dashboard services
    clientBuilder.AddDashboard();
});

var dashboardApp = dashboardBuilder.Build();

// Map dashboard endpoints
dashboardApp.MapOrleansDashboard();

await dashboardApp.RunAsync();

await siloHost.StopAsync();
