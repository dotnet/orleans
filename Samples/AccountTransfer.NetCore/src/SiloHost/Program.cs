using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Configuration;
using Orleans.Hosting;
using AccountTransfer.Grains;
using Orleans.Transactions;
using Orleans.Transactions.Development;

namespace OrleansSiloHost
{
    public class Program
    {
        public static int Main(string[] args)
        {
            return RunMainAsync().Result;
        }

        private static async Task<int> RunMainAsync()
        {
            try
            {
                var host = await StartSilo();
                Console.WriteLine("Press Enter to terminate...");
                Console.ReadLine();

                await host.StopAsync();

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return 1;
            }
        }

        private static async Task<ISiloHost> StartSilo()
        {
            // define the cluster configuration
            var config = ClusterConfiguration.LocalhostPrimarySilo();
            config.AddMemoryStorageProvider();

            var builder = new SiloHostBuilder()
                .UseConfiguration(config)
                .AddApplicationPartsFromReferences(typeof(ATMGrain).Assembly)
                .ConfigureLogging(logging => logging.AddConsole())
                .UseInClusterTransactionManager(new TransactionsConfiguration())
                .UseInMemoryTransactionLog()
                .UseTransactionalState();

            var host = builder.Build();
            await host.StartAsync();
            return host;
        }
    }
}
