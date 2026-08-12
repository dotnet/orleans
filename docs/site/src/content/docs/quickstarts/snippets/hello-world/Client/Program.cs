// <first_orleans_app_client_program>
using GrainInterfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;

var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleansClient(clientBuilder =>
{
    clientBuilder.UseLocalhostClustering();
});

using var host = builder.Build();
await host.StartAsync();

var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
var friend = grainFactory.GetGrain<IHello>("friend");
var response = await friend.SayHello("Hi friend");

Console.WriteLine(response);

await host.StopAsync();
// </first_orleans_app_client_program>
