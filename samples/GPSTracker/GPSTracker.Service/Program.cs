using GPSTracker;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Net;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
            .AddPrometheusExporter()
            .AddMeter("Microsoft.Orleans"))
    .WithTracing(tracing =>
    {
        // Set a service name
        tracing.SetResourceBuilder(
            ResourceBuilder.CreateDefault()
                .AddService(serviceName: "GPSTracker", serviceVersion: "1.0"));

        tracing.AddSource("Microsoft.Orleans.Runtime");
        tracing.AddSource("Microsoft.Orleans.Application");

#pragma warning disable CS0618 // Preserve the sample's Zipkin endpoint until the exporter is removed.
        tracing.AddZipkinExporter(zipkin => zipkin.Endpoint = new Uri("http://localhost:9411/api/v2/spans"));
#pragma warning restore CS0618
    });

builder.Host.UseOrleans((ctx, siloBuilder) => {

    // In order to support multiple hosts forming a cluster, they must listen on different ports.
    // Use the --InstanceId X option to launch subsequent hosts.
    int instanceId = ctx.Configuration.GetValue<int>("InstanceId");
    siloBuilder.UseLocalhostClustering(
        siloPort: 11111 + instanceId,
        gatewayPort: 30000 + instanceId,
        primarySiloEndpoint: new IPEndPoint(IPAddress.Loopback, 11111));

    siloBuilder.AddActivityPropagation();
});
builder.WebHost.UseKestrel((ctx, kestrelOptions) =>
{
    // To avoid port conflicts, each Web server must listen on a different port.
    int instanceId = ctx.Configuration.GetValue<int>("InstanceId");
    kestrelOptions.ListenLocalhost(5001 + instanceId);
});
builder.Services.AddHostedService<HubListUpdater>();
builder.Services.AddSignalR().AddJsonProtocol();

WebApplication app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.MapHub<LocationHub>("/locationHub");
app.MapPrometheusScrapingEndpoint();
app.Run();
