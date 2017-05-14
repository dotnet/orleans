using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Orleans.Runtime.Configuration;

namespace OrleansSilo
{
    public class Program
    {
        private static OrleansHostWrapper hostWrapper;

        static int Main(string[] args)
        {
            int exitCode = InitializeOrleans();

            Console.WriteLine("Press Enter to terminate...");
            Console.ReadLine();

            exitCode += ShutdownSilo();

            return exitCode;
        }

        private static int InitializeOrleans()
        {
            var config = new ClusterConfiguration();
            config.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain;
            config.Globals.ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain;
            var ep = new IPEndPoint(IPAddress.Loopback, 10300);
            config.Globals.SeedNodes.Add(ep);
            config.PrimaryNode = ep;
            config.Defaults.PropagateActivityId = true;
            config.Defaults.ProxyGatewayEndpoint = new IPEndPoint(IPAddress.Any, 10400);
            config.Defaults.Port = 10300;
            var ips = Dns.GetHostAddressesAsync(Dns.GetHostName()).Result;
            config.Defaults.HostNameOrIPAddress = ips.FirstOrDefault()?.ToString();
            config.UseStartupType<Startup>();
            hostWrapper = new OrleansHostWrapper(config);
            return hostWrapper.Run();
        }

        private static int ShutdownSilo()
        {
            if (hostWrapper != null)
            {
                return hostWrapper.Stop();
            }
            return 0;
        }
    }
}