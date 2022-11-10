using AdventureGrainInterfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using var host = Host.CreateDefaultBuilder(args)
    .UseOrleansClient(clientBuilder =>
        clientBuilder.UseLocalhostClustering())
    .Build();

await host.StartAsync();

Console.WriteLine("""
     ______      __                         __                           
    /\  _  \    /\ \                       /\ \__                        
    \ \ \L\ \   \_\ \  __  __     __    ___\ \ ,_\  __  __  _ __    __   
     \ \  __ \  /'_` \/\ \/\ \  /'__`\/' _ `\ \ \/ /\ \/\ \/\`'__\/'__`\ 
      \ \ \/\ \/\ \L\ \ \ \_/ |/\  __//\ \/\ \ \ \_\ \ \_\ \ \ \//\  __/ 
       \ \_\ \_\ \___,_\ \___/ \ \____\ \_\ \_\ \__\\ \____/\ \_\\ \____\
        \/_/\/_/\/__,_ /\/__/   \/____/\/_/\/_/\/__/ \/___/  \/_/ \/____/
    """);

Console.WriteLine();
Console.WriteLine("What's your name?");
var name = Console.ReadLine()!;

var client = host.Services.GetRequiredService<IClusterClient>();
var player = client.GetGrain<IPlayerGrain>(Guid.NewGuid());
await player.SetName(name);

var room1 = client.GetGrain<IRoomGrain>(0);
await player.SetRoomGrain(room1);

Console.WriteLine(await player.Play("look"));

var result = "Start";
try
{
    while (result is not "")
    {
        var command = Console.ReadLine()!;

        result = await player.Play(command);
        Console.WriteLine(result);
    }
}
finally
{
    await player.Die();
    Console.WriteLine("Game over!");
    await host.StopAsync();
}
