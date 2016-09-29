using Microsoft.WindowsAzure.ServiceRuntime;
using Orleans.Runtime.Host;
using System.Diagnostics;
using System.Net;

namespace GPSTracker.Web
{
    public class WebRole : RoleEntryPoint
    {
        AzureSilo silo;

        public override bool OnStart()
        {
            // Set the maximum number of concurrent connections 
            ServicePointManager.DefaultConnectionLimit = 12;

            Trace.WriteLine("Starting Role Entry Point");

            silo = new AzureSilo();

            return silo.Start();
        }

        public override void OnStop() { silo.Stop(); }
        public override void Run() { silo.Run(); }
    }
}
