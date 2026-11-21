using DurableOrderProcessing;
using Orleans.Dashboard;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<OrderingOptions>(builder.Configuration.GetSection("Ordering"));
builder.Services.AddOrderProcessingApi();

// The order workflows call the simulated payment gateway, which this application serves under /gateway, over HTTP.
builder.UseOrleans(silo => silo
    .UseLocalhostClustering()
    .AddOrderProcessing(payments => payments.ConfigureHttpClient(client =>
        client.BaseAddress = new Uri(builder.Configuration["Payments:BaseAddress"]!)))
    .AddDashboard());

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapOrleansDashboard(routePrefix: "/dashboard");
app.MapOrderProcessing();

app.Run();
