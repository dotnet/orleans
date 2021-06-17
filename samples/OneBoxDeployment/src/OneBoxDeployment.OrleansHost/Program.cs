using OneBoxDeployment.Grains;
using OneBoxDeployment.OrleansUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Statistics;
using System;
using System.Net;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

namespace OneBoxDeployment.OrleansHost
{
    /// <summary>
    /// The Orleans console host program.
    /// </summary>
    public static class Program
    {
        private static AutoResetEvent ProceedWithClosing = new AutoResetEvent(false);


        /// <summary>
        /// The main Orleans console host entry point.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public static async Task Main(string[] args)
        {
            var siloHost = BuildOrleansHost(args, null);

            Console.CancelKeyPress += (sender, e) => Environment.Exit(0);
            AssemblyLoadContext.Default.Unloading += _ => ProceedWithClosing.Set();

            //Note, there could be parameters to start more than one silo. This would
            //require refactoring to build a class to handle such a structure
            //and use it also in the tests.
            await siloHost.StartAsync().ConfigureAwait(false);
            Console.WriteLine("Application started. Press Ctrl+C to shut down.");
            ProceedWithClosing.WaitOne();

            await siloHost.StopAsync().ConfigureAwait(false);
        }


        /// <summary>
        /// A method to build Orleans host.
        /// </summary>
        /// <param name="args">The arguments to use to build the host.</param>
        /// <returns>A built Orleans silo host.</returns>
        public static ISiloHost BuildOrleansHost(string[] args, ClusterConfig clusterConfig)
        {
            var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.orleanshost.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.orleanshost.{environmentName}.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .AddCommandLine(args ?? Array.Empty<string>())
                .AddInMemoryCollection()
                .Build();

            //If cluster configuration is null, the application is likely started from the command line,
            //so try read one from a JSON configuration file. Also, test sets args == null.
            if(clusterConfig == null)
            {
                clusterConfig = configuration.GetSection("ClusterConfig").Get<ClusterConfig>();
            }

            var siloBuilder = new SiloHostBuilder()
                .ConfigureLogging((hostingContext, logging) =>
                {
                    //TODO: Logging slows down testing a bit.
                    //logging.AddConfiguration(hostingContext.Configuration.GetSection("Logging"));
                    logging.AddConsole();
                    logging.AddDebug();
                })
                .UsePerfCounterEnvironmentStatistics()
                .Configure<ClusterOptions>(options =>
                {
                    options.ClusterId = clusterConfig.ClusterOptions.ClusterId;
                    options.ServiceId = clusterConfig.ClusterOptions.ServiceId;
                })
                .UseAdoNetClustering(options =>
                {
                    options.Invariant = clusterConfig.ConnectionConfig.AdoNetConstant;
                    options.ConnectionString = clusterConfig.ConnectionConfig.ConnectionString;
                })
                .Configure<EndpointOptions>(options =>
                {
                    options.AdvertisedIPAddress = clusterConfig.EndPointOptions.AdvertisedIPAddress ?? IPAddress.Loopback;
                    options.GatewayListeningEndpoint = clusterConfig.EndPointOptions.GatewayListeningEndpoint;
                    options.GatewayPort = clusterConfig.EndPointOptions.GatewayPort;
                    options.SiloListeningEndpoint = clusterConfig.EndPointOptions.SiloListeningEndpoint;
                    options.SiloPort = clusterConfig.EndPointOptions.SiloPort;
                })
                .Configure<SiloMessagingOptions>(options =>
                {
                    options.ResponseTimeout = TimeSpan.FromSeconds(5);
                })
                .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(TestStateGrain).Assembly).WithReferences())
                .UseAdoNetReminderService(options =>
                {
                    options.Invariant = clusterConfig.ReminderConfigs[0].AdoNetConstant;
                    options.ConnectionString = clusterConfig.ReminderConfigs[0].ConnectionString;
                })
                .AddAdoNetGrainStorage(clusterConfig.StorageConfigs[0].Name, options =>
                {
                    options.Invariant = clusterConfig.StorageConfigs[0].AdoNetConstant;
                    options.ConnectionString = clusterConfig.StorageConfigs[0].ConnectionString;
                });

            return siloBuilder.Build();
        }
    }
}
