using System.Net;

await Host.CreateDefaultBuilder(args)
    .UseOrleans((ctx, siloBuilder) =>
    {
        // In order to support multiple hosts forming a cluster, they must listen on different ports.
        // Use the --InstanceId X option to launch subsequent hosts.
        var instanceId = ctx.Configuration.GetValue<int>("InstanceId");
        var port = 11_111;
        siloBuilder.UseLocalhostClustering(
            siloPort: port + instanceId,
            gatewayPort: 30000 + instanceId,
            primarySiloEndpoint: new IPEndPoint(IPAddress.Loopback, port));
    })
    .ConfigureWebHostDefaults(webBuilder =>
    {
        webBuilder.UseStartup<Startup>();
        webBuilder.ConfigureKestrel((ctx, kestrelOptions) =>
        {
            // To avoid port conflicts, each Web server must listen on a different port.
            var instanceId = ctx.Configuration.GetValue<int>("InstanceId");
            kestrelOptions.ListenLocalhost(5000 + instanceId);
        });
    })
    .RunConsoleAsync();
