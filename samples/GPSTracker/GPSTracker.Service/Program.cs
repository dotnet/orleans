using Orleans.Hosting;
using GPSTracker;
using System.Net;
var builder = WebApplication.CreateBuilder(args);

builder.Host.UseOrleans((ctx, siloBuilder) => {

    // In order to support multiple hosts forming a cluster, they must listen on different ports.
    // Use the --InstanceId X option to launch subsequent hosts.
    var instanceId = ctx.Configuration.GetValue<int>("InstanceId");
    siloBuilder.UseLocalhostClustering(
        siloPort: 11111 + instanceId,
        gatewayPort: 30000 + instanceId,
        primarySiloEndpoint: new IPEndPoint(IPAddress.Loopback, 11111));

});
builder.WebHost.ConfigureKestrel((ctx, kestrelOptions) =>
{
    // To avoid port conflicts, each Web server must listen on a different port.
    var instanceId = ctx.Configuration.GetValue<int>("InstanceId");
    kestrelOptions.ListenLocalhost(5001 + instanceId);
});
builder.Services.AddHostedService<HubListUpdater>();
builder.Services.AddSignalR().AddJsonProtocol();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
app.UseStaticFiles();
app.UseDefaultFiles();
app.UseRouting();

app.UseAuthorization();

app.UseEndpoints(endpoints =>
{
    endpoints.MapHub<LocationHub>("/locationHub");
});
app.Run();
