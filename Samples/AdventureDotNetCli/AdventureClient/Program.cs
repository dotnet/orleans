using AdventureGrainInterfaces;
using Orleans;
using System;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
using System.Threading.Tasks;

namespace AdventureClient
{
   
    class Program
    {

        static IAsyncStream<string> _playerStream;
        static gameStreamObserver _gsObserver;
        static StreamSubscriptionHandle<string> _subscriptionHandle;

        static void Main(string[] args)
        {
            var config = ClientConfiguration.LocalhostSilo();
            // Adding stream support
            config.AddSimpleMessageStreamProvider("SMS");

            GrainClient.Initialize(config);

            MainAsync(args).Wait(-1); // Wait for ever, never timeout
        }

        private static async Task MainAsync(string[] args)
        {
       

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

            Guid playerGuid = Guid.NewGuid();

            var player = GrainClient.GrainFactory.GetGrain<IPlayerGrain>(playerGuid);
            player.SetName(name).Wait();

            await RegisterPlayerStream(playerGuid);

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
                player.Die(null, null).Wait();
                Console.WriteLine("Game over!");
            }
        }

        private static async Task RegisterPlayerStream(Guid playerGuid)
        {
            // Neded to make variables global to avoid garbage-collection and disconnect.
            var sp = GrainClient.GetStreamProvider("SMS");
            _playerStream = sp.GetStream<string>(playerGuid, "playerstream");
            
            //await playerStream.SubscribeAsync<string>((data, token) => Console.WriteLine(data));
            
            _gsObserver = new gameStreamObserver();
            _subscriptionHandle = await _playerStream.SubscribeAsync(_gsObserver);
        }

        public class gameStreamObserver : Orleans.Streams.IAsyncObserver<string>
        {
            public Task OnCompletedAsync()
            {
                throw new NotImplementedException();
            }

            public Task OnErrorAsync(Exception ex)
            {
                throw new NotImplementedException();
            }

            public Task OnNextAsync(string item, StreamSequenceToken token = null)
            {
                Console.WriteLine(item);
                return TaskDone.Done;
            }
        }

    }
}
