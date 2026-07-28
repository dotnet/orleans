// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

var builder = WebApplication.CreateBuilder(args);

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

if (builder.Environment.IsDevelopment())
{
    builder.Host.UseOrleans((_, builder) => builder
            .UseLocalhostClustering()
            .AddMemoryGrainStorage("shopping-cart")
            .AddStartupTask<SeedProductStoreTask>());
}
else
{
    builder.Host.UseOrleans((context, builder) =>
    {
        var connectionString = context.Configuration["ORLEANS_AZURE_COSMOS_DB_CONNECTION_STRING"]!;

        builder.Configure<ClusterOptions>(options =>
        {
            options.ClusterId = "ShoppingCartCluster";
            options.ServiceId = nameof(ShoppingCartService);
        });

        builder
            .UseCosmosClustering(o => o.ConfigureCosmosClient(connectionString))
            .AddCosmosGrainStorage("shopping-cart", o => o.ConfigureCosmosClient(connectionString));
    });
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
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
