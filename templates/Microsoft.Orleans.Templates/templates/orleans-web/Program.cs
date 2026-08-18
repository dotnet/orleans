using OrleansWebApp;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseOrleans(siloBuilder => siloBuilder.UseLocalhostClustering());

var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/hello/world"));
app.MapGet("/hello/{name}", async (string name, IGrainFactory grainFactory) =>
{
    var grain = grainFactory.GetGrain<IHelloGrain>(name);
    return await grain.SayHello(name);
});

await app.RunAsync();
