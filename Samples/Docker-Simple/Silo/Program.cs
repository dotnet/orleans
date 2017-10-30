using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Silo
{
    class Program
    {
        private static ISiloHost silo;
        private static readonly AutoResetEvent closing = new AutoResetEvent(false);

        static void Main(string[] args)
        {
            var connectionString = File.ReadAllText("connection-string.txt");

            var config = new ClusterConfiguration();
            config.Globals.DataConnectionString = connectionString;
            config.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.AzureTable;
            config.Globals.ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.AzureTable;
            config.Globals.FastKillOnCancelKeyPress = true;
            config.Defaults.Port = 11111;
            config.Defaults.ProxyGatewayEndpoint = new IPEndPoint(IPAddress.Any, 30000);

            silo = new SiloHostBuilder()
                .AddApplicationPartsFromAppDomain()
                .AddApplicationPartsFromBasePath()
                .UseConfiguration(config)
                .ConfigureLogging(builder => builder.SetMinimumLevel(LogLevel.Warning).AddConsole())
                .Build();

            Task.Run(StartSilo);

            AppDomain.CurrentDomain.ProcessExit += (object sender, EventArgs e) =>
            {
                Console.WriteLine("ProcessExit fired");
                Task.Run(StopSilo);
            };

            closing.WaitOne();
        }

        private static async Task StartSilo()
        {
            await silo.StartAsync();
            Console.WriteLine("Silo started");
        }

        private static async Task StopSilo()
        {
            await silo.StopAsync();
            Console.WriteLine("Silo stopped");
            closing.Set();
        }
    }
}
