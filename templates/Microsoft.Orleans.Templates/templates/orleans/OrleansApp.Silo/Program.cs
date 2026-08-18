using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(siloBuilder => siloBuilder.UseLocalhostClustering());

await builder.Build().RunAsync();
