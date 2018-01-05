using Orleans;
using Orleans.Runtime.Configuration;
using OrleansGrainInterfaces;
using System;
using System.Net;
using System.Threading.Tasks;

namespace OrleansClient
{
    class Program
    {
        private static IClusterClient client;
        private static bool running;
        static void Main(string[] args)
        {
            Task.Run(() => InitializeOrleans());
            Console.WriteLine("Enter to stop.");
            Console.ReadLine();
            running = false;
        }

        static async Task InitializeOrleans()
        {
            //var config = new ClusterConfiguration();
            //config.Globals.DataConnectionString = "Server=tnwli-pc.dmtnprod.lan;Database=ApprovalSystem;User Id=QTIP;Password=QTIP; ";
            //config.Globals.ClusterId = "AprovalSiloID";
            //config.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.SqlServer;
            //config.Globals.ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.SqlServer;
            //config.Defaults.PropagateActivityId = true;
            //config.Defaults.ProxyGatewayEndpoint = new System.Net.IPEndPoint(IPAddress.Any, 18000);


            var config = new ClientConfiguration();
            config.ClusterId = "AprovalSiloID";
            config.PropagateActivityId = true;
            config.AdoInvariant = "System.Data.SqlClient";

            config.DataConnectionString = "Server=tnwli-pc.dmtnprod.lan;Database=ApprovalSystem;User Id=QTIP;Password=QTIP; ";
            config.GatewayProvider = ClientConfiguration.GatewayProviderType.SqlServer;
            Console.WriteLine("Initializing ... ");
            client = new ClientBuilder()
                .ConfigureApplicationParts(p => p.AddFromAppDomain().AddFromApplicationBaseDirectory())
                .UseConfiguration(config).Build();
            try
            {
                await client.Connect();
                running = true;
                Console.WriteLine("Initialized.");
                var grain = client.GetGrain<IApproval<string>>(Guid.Empty);
                while (running)
                {
                    string proposal = "ACED Proposal";
                    var response = await grain.Reject(proposal);
                    Console.WriteLine($"{proposal} was Approved : { response}");
                    await Task.Delay(1000);
                }
                client.Dispose();
            }
            catch(Exception ex)
            {
                throw ex;
            }
        }
    }
}
