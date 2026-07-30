// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

using Azure.Data.Tables;
using Azure.Identity;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.UseOrleans(siloBuilder =>
    {
        siloBuilder.UseLocalhostClustering().AddMemoryGrainStorage("shopping-cart");
    });
}
else
{
    var clusterId = builder.Configuration["ORLEANS_CLUSTER_ID"]
        ?? throw new InvalidOperationException("ORLEANS_CLUSTER_ID is not configured.");

    builder.UseOrleans(siloBuilder =>
    {
#pragma warning disable ORLEANSEXP003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        var endpointAddress = IPAddress.Parse(builder.Configuration["WEBSITE_PRIVATE_IP"]!);
        var strPorts = builder.Configuration["WEBSITE_PRIVATE_PORTS"]!.Split(',');
        if (strPorts.Length < 2)
        {
            var env = Environment.GetEnvironmentVariable("WEBSITE_PRIVATE_PORTS");
            throw new Exception($"Insufficient private ports configured: WEBSITE_PRIVATE_PORTS: '{builder.Configuration["WEBSITE_PRIVATE_PORTS"]?.ToString()}' or '{env}.");
        }

        var (siloPort, gatewayPort) = (int.Parse(strPorts[0]), int.Parse(strPorts[1]));

        siloBuilder.ConfigureEndpoints(endpointAddress, siloPort, gatewayPort, listenOnAnyHostAddress: true)
        .Configure<ClusterOptions>(
            options =>
            {
                options.ClusterId = clusterId;
                options.ServiceId = nameof(ShoppingCartService);
            })
        .UseAzureStorageClustering(
            options =>
            {
                options.TableServiceClient = new TableServiceClient(new Uri(builder.Configuration["ORLEANS_AZURE_STORAGE_URI"]!), new DefaultAzureCredential());
                options.TableName = $"{clusterId}Clustering";
            })
        .AddAzureTableGrainStorage("shopping-cart",
            options =>
            {
                options.TableServiceClient = new TableServiceClient(new Uri(builder.Configuration["ORLEANS_AZURE_STORAGE_URI"]!), new DefaultAzureCredential());
                options.TableName = $"{clusterId}Persistence";
            });
    });
}

var services = builder.Services;
services.AddMudServices();
services.AddRazorPages();
services.AddServerSideBlazor();
services.AddHttpContextAccessor();
services.AddSingleton<ShoppingCartService>();
services.AddSingleton<InventoryService>();
services.AddSingleton<ProductService>();
services.AddScoped<ComponentStateChangedObserver>();
services.AddSingleton<ToastService>();
services.AddLocalStorageServices();
var appInsightsConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrEmpty(appInsightsConnectionString))
{
    services.AddApplicationInsights("Silo");
    builder.Logging.AddApplicationInsights((telemetry) => telemetry.ConnectionString = appInsightsConnectionString, logger => { });
}

builder.Services.AddHostedService<ProductStoreSeeder>();

var app = builder.Build();

if (builder.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
await app.RunAsync();
