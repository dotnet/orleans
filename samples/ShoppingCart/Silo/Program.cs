// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.
var builder = WebApplication.CreateBuilder(args);


builder.Host
    .UseOrleans(
        (context, builder) =>
        {
            if (context.HostingEnvironment.IsDevelopment())
            {
                builder.UseLocalhostClustering()
                    .AddMemoryGrainStorage("shopping-cart")
                    .AddStartupTask<SeedProductStoreTask>();
            }
            else
            {
                var endpointAddress =
                    IPAddress.Parse(context.Configuration["WEBSITE_PRIVATE_IP"]);
                var strPorts =
                    context.Configuration["WEBSITE_PRIVATE_PORTS"].Split(',');
                if (strPorts.Length < 2)
                    throw new Exception("Insufficient private ports configured.");
                var (siloPort, gatewayPort) =
                    (int.Parse(strPorts[0]), int.Parse(strPorts[1]));
                var connectionString =
                    context.Configuration["ORLEANS_AZURE_STORAGE_CONNECTION_STRING"];

                builder
                    .ConfigureEndpoints(endpointAddress, siloPort, gatewayPort)
                    .Configure<ClusterOptions>(
                        options =>
                        {
                            options.ClusterId = "ShoppingCartCluster";
                            options.ServiceId = nameof(ShoppingCartService);
                        }).UseAzureStorageClustering(
                    options => options.ConfigureTableServiceClient(connectionString));
                builder.AddAzureTableGrainStorage(
                    "shopping-cart",
                    options => options.ConfigureTableServiceClient(connectionString));
            }
        });

builder.Services.AddMudServices();
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ShoppingCartService>();
builder.Services.AddSingleton<InventoryService>();
builder.Services.AddSingleton<ProductService>();
builder.Services.AddScoped<ComponentStateChangedObserver>();
builder.Services.AddSingleton<ToastService>();
builder.Services.AddLocalStorageServices();


var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();