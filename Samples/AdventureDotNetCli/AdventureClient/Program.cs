using AdventureGrainInterfaces;
using Orleans;
using System;
using Orleans.Runtime.Configuration;

namespace AdventureClient
{
    class Program
    {
        static void Main(string[] args)
        {
            var config = ClientConfiguration.LocalhostSilo();
            GrainClient.Initialize(config);

            Console.WriteLine(@"
  ___      _                 _                  
 / _ \    | |               | |                 
/ /_\ \ __| |_   _____ _ __ | |_ _   _ _ __ ___ 
|  _  |/ _` \ \ / / _ \ '_ \| __| | | | '__/ _ \
| | | | (_| |\ V /  __/ | | | |_| |_| | | |  __/
\_| |_/\__,_| \_/ \___|_| |_|\__|\__,_|_|  \___|");

            Console.WriteLine();
            Console.WriteLine("What's you name?");
            string name = Console.ReadLine();

            var player = GrainClient.GrainFactory.GetGrain<IPlayerGrain>(Guid.NewGuid());
            player.SetName(name).Wait();
            var room1 = GrainClient.GrainFactory.GetGrain<IRoomGrain>(0);
            player.SetRoomGrain(room1).Wait();

            Console.WriteLine(player.Play("look").Result);

            string result = "Start";

            try
            {
                while (result != "")
                {
                    string command = Console.ReadLine();

                    result = player.Play(command).Result;
                    Console.WriteLine(result);
                }
            }
            finally
            {
                player.Die().Wait();
                Console.WriteLine("Game over!");
            }
        }
    }
}
