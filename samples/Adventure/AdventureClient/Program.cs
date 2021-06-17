using AdventureGrainInterfaces;
using Orleans;
using System;

using var client = new ClientBuilder()
    .UseLocalhostClustering()
    .Build();

await client.Connect();

Console.WriteLine(@"
  ___      _                 _                  
 / _ \    | |               | |                 
/ /_\ \ __| |_   _____ _ __ | |_ _   _ _ __ ___ 
|  _  |/ _` \ \ / / _ \ '_ \| __| | | | '__/ _ \
| | | | (_| |\ V /  __/ | | | |_| |_| | | |  __/
\_| |_/\__,_| \_/ \___|_| |_|\__|\__,_|_|  \___|");

Console.WriteLine();
Console.WriteLine("What's your name?");
string name = Console.ReadLine();

var player = client.GetGrain<IPlayerGrain>(Guid.NewGuid());
await player.SetName(name);

var room1 = client.GetGrain<IRoomGrain>(0);
await player.SetRoomGrain(room1);

Console.WriteLine(await player.Play("look"));

string result = "Start";

try
{
    while (result != "")
    {
        string command = Console.ReadLine();

        result = await player.Play(command);
        Console.WriteLine(result);
    }
}
finally
{
    await player.Die();
    Console.WriteLine("Game over!");
    await client.Close();
}
