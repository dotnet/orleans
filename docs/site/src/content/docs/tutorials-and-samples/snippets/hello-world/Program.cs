// <hello_world_program>
using HelloWorld;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;

var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.UseLocalhostClustering();
});

using var host = builder.Build();
await host.StartAsync();

var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
var friend = grainFactory.GetGrain<IHello>("friend");
var response = await friend.SayHello("Hi friend");

Console.WriteLine(response);

await host.StopAsync();
// </hello_world_program>
