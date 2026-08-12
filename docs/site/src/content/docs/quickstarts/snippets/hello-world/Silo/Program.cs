// <first_orleans_app_silo_program>
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.UseLocalhostClustering();
});

using var host = builder.Build();
await host.RunAsync();
// </first_orleans_app_silo_program>
