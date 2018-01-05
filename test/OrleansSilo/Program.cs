using Orleans.Runtime.Configuration;
using System;
using System.Linq;
using System.Net;

namespace OrleansSilo
{
    class Program
    {
        private static OrleansHostWrapper _hostWrapper;
        static int Main(string[] args)
        {
            int exitCode = InitializeOrleans();
            Console.WriteLine("Press enter to shutdown silo.");
            Console.ReadLine();
            exitCode += ShutDownSilo();
            return exitCode;
        }

        private static int ShutDownSilo()
        {
            if(_hostWrapper != null)
            {
                return _hostWrapper.Stop();
            }
            return 0;
        }

        private static int InitializeOrleans()
        {
            var config = new ClusterConfiguration();
            config.Globals.DataConnectionString = "Server=tnwli-pc.dmtnprod.lan;Database=ApprovalSystem;User Id=QTIP;Password=QTIP; ";
            config.Globals.ClusterId = "AprovalSiloID";
            config.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.SqlServer;
            config.Globals.ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.SqlServer;
            config.Defaults.PropagateActivityId = true;
            config.Defaults.ProxyGatewayEndpoint = new System.Net.IPEndPoint(IPAddress.Any, 18000);

            config.Defaults.Port = 18100;
            var ips = Dns.GetHostAddressesAsync(Dns.GetHostName()).Result;
            config.Defaults.HostNameOrIPAddress = ips.FirstOrDefault()?.ToString();
            _hostWrapper = new OrleansHostWrapper(config);
            return _hostWrapper.Run();
        }

    }
}
