// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication;
using Orleans.Configuration;
using Orleans.ShoppingCart.Silo.Authentication;
using Orleans.ShoppingCart.Silo.Authorization;
using Orleans.ShoppingCart.Silo.Health;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            .AddMemoryGrainStorage("shopping-cart");
    });
}
else
{
    ConfigureProductionOrleans(builder);
}

var services = builder.Services;
services.AddMudServices();
services.AddRazorPages();
services.AddServerSideBlazor();
services.AddHttpContextAccessor();
services
    .AddAuthentication(AppServiceAuthenticationDefaults.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, AppServiceAuthenticationHandler>(
        AppServiceAuthenticationDefaults.AuthenticationScheme,
        _ => { });
services.AddAuthorization(options =>
    options.AddPolicy(
        AuthorizationPolicies.ProductManagement,
        policy => policy
            .RequireAuthenticatedUser()
            .RequireRole(AuthorizationPolicies.ProductAdministratorRole)));
services.AddSingleton<ShoppingCartService>();
services.AddSingleton<InventoryService>();
services.AddScoped<ProductService>();
services.AddScoped<ComponentStateChangedObserver>();
services.AddScoped<ToastService>();
services.AddLocalStorageServices();
services.AddApplicationInsights("ShoppingCart");
services.AddHostedService<ProductStoreSeeder>();
services.AddSingleton<AppServiceLifecycle>();
services.AddHostedService(provider => provider.GetRequiredService<AppServiceLifecycle>());
services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(30);
});

var appInsightsConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
{
    builder.Logging.AddApplicationInsights(
        telemetry => telemetry.ConnectionString = appInsightsConnectionString,
        _ => { });
}

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
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok());
app.MapGet(
    "/health/ready",
    (AppServiceLifecycle lifecycle) =>
        lifecycle.IsReady ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

await app.RunAsync();

static void ConfigureProductionOrleans(WebApplicationBuilder builder)
{
    var clusterId = GetRequiredSetting(builder, "ORLEANS_CLUSTER_ID");
    var serviceId = GetRequiredSetting(builder, "ORLEANS_SERVICE_ID");
    var storageUri = new Uri(GetRequiredSetting(builder, "ORLEANS_AZURE_STORAGE_URI"));
    var managedIdentityClientId = GetRequiredSetting(builder, "AZURE_CLIENT_ID");
    var privateIp = IPAddress.Parse(GetRequiredSetting(builder, "WEBSITE_PRIVATE_IP"));
    var privatePorts = GetRequiredSetting(builder, "WEBSITE_PRIVATE_PORTS")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    if (privatePorts.Length < 1
        || !int.TryParse(privatePorts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var siloPort))
    {
        throw new InvalidOperationException(
            "WEBSITE_PRIVATE_PORTS must contain at least one TCP port.");
    }

    var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ManagedIdentityClientId = managedIdentityClientId,
    });
    var tableServiceClient = new TableServiceClient(storageUri, credential);

    builder.UseOrleans(siloBuilder =>
    {
        siloBuilder
            .Configure<SiloOptions>(options =>
            {
                options.SiloName = builder.Configuration["WEBSITE_INSTANCE_ID"]
                    ?? Environment.MachineName;
            })
            .Configure<ClusterOptions>(options =>
            {
                options.ClusterId = clusterId;
                options.ServiceId = serviceId;
            })
            .ConfigureEndpoints(
                privateIp,
                siloPort,
                gatewayPort: 0,
                listenOnAnyHostAddress: true)
            .UseAzureStorageClustering(options =>
            {
                options.TableServiceClient = tableServiceClient;
                options.TableName = $"{clusterId}Clustering";
            })
            .AddAzureTableGrainStorage(
                "shopping-cart",
                options =>
                {
                    options.TableServiceClient = tableServiceClient;
                    options.TableName = $"{clusterId}Persistence";
                });
    });
}

static string GetRequiredSetting(WebApplicationBuilder builder, string name) =>
    builder.Configuration[name] is { } value && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new InvalidOperationException($"The required setting '{name}' isn't configured.");
