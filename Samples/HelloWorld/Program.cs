using System;
using HelloWorld;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;

using var host = new HostBuilder()
    .UseOrleans(builder =>
    {
        builder.UseLocalhostClustering();
    })
    .Build();

await host.StartAsync();

var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
var friend = grainFactory.GetGrain<IHelloGrain>(0);
var result = friend.SayHello("Good morning!"); 
Console.WriteLine("\n\n{0}\n\n", result);

Console.WriteLine("Orleans is running.\nPress Enter to terminate...");
Console.ReadLine();
Console.WriteLine("Orleans is stopping...");

await host.StopAsync();
