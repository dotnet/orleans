using Orleans;
using System;
using System.IO;
using System.Reflection;
using Orleans.Runtime.Configuration;

namespace AdventureSetup
{
    class Program
    {
        static int Main(string [] args)
        {
            var path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string mapFileName = Path.Combine (path, "AdventureMap.json");

            switch (args.Length)
            {
                default:
                    Console.WriteLine("*** Invalid command line arguments.");
                    return -1;
                case 0:
                    break;
                case 1:
                    mapFileName = args[0];
                    break;
            }

            if (!File.Exists(mapFileName))
            {
                Console.WriteLine("*** File not found: {0}", mapFileName);
                return -2;
            }

             // The Orleans silo environment is initialized in its own app domain in order to more
            // closely emulate the distributed situation, when the client and the server cannot
            // pass data via shared memory.
            AppDomain hostDomain = AppDomain.CreateDomain("OrleansHost", null, new AppDomainSetup
            {
                AppDomainInitializer = InitSilo,
                AppDomainInitializerArguments = args,
            });

            var config = ClientConfiguration.LocalhostSilo();
            GrainClient.Initialize(config);

            Console.WriteLine("Map file name is '{0}'.", mapFileName);
            Console.WriteLine("Setting up Adventure, please wait ...");
            Adventure adventure = new Adventure();     
            adventure.Configure(mapFileName).Wait();
            Console.WriteLine("Adventure setup completed.");
            Console.ReadLine();

            hostDomain.DoCallBack(ShutdownSilo);
                        
            return 0;
        }

        static void InitSilo(string[] args)
        {
            hostWrapper = new OrleansHostWrapper(args);

            if (!hostWrapper.Run())
            {
                Console.Error.WriteLine("Failed to initialize Orleans silo");
            }
        }

        static void ShutdownSilo()
        {
            if (hostWrapper != null)
            {
                hostWrapper.Dispose();
                GC.SuppressFinalize(hostWrapper);
            }
        }

        private static OrleansHostWrapper hostWrapper;
    }
}
