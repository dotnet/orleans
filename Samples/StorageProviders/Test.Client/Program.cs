using System;
using Host;
using Orleans;
using Orleans.Runtime.Configuration;
using Test.Interfaces;

namespace Test.Client
{
    /// <summary>
    /// Orleans test silo host
    /// </summary>
    public class Program
    {
        static void Main(string[] args)
        {
            // The Orleans environment is initialized in its own app domain in order to more
            // closely emulate the distributed situation, when the client and the server cannot
            // pass data via shared memory.
            AppDomain hostDomain = AppDomain.CreateDomain("OrleansHost", null, new AppDomainSetup
            {
                AppDomainInitializer = InitSilo,
                AppDomainInitializerArguments = args,
            });

            var config = ClientConfiguration.LocalhostSilo();
            GrainClient.Initialize(config);

            var grain = GrainClient.GrainFactory.GetGrain<IPerson>(0);

            // If the name is set, we've run this code before.
            var name = grain.GetFirstName().Result;

            if ( name != null)
            {
                Console.WriteLine("\n\nThis was found in the persistent store: {0}, {1}, {2}\n\n",
                    name, 
                    grain.GetLastName().Result, 
                    grain.GetGender().Result.ToString());
            }
            else
            {
                grain.SetPersonalAttributes(new PersonalAttributes { FirstName = "John", LastName = "Doe", Gender = GenderType.Male }).Wait();
                Console.WriteLine("\n\nWe just wrote something to the persistent store. Please verify!\n\n");
            }

            Console.WriteLine("Orleans Silo is running.\nPress Enter to terminate...");
            Console.ReadLine();

            hostDomain.DoCallBack(ShutdownSilo);
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
