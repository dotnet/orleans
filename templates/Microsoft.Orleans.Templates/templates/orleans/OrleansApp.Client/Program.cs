using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrleansApp.Contracts;

var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleansClient(clientBuilder => clientBuilder.UseLocalhostClustering());

using var host = builder.Build();
await host.StartAsync();

var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
var friend = grainFactory.GetGrain<IHelloGrain>("friend");
Console.WriteLine(await friend.SayHello("friend"));

await host.StopAsync();
