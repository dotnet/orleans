using System.Net;
using Microsoft.WindowsAzure.ServiceRuntime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;

namespace OrleansXO.WorkerRole
{
    public class WorkerRole : RoleEntryPoint
    {
        AzureSilo silo; 

        public override bool OnStart()
        {
            // Set the maximum number of concurrent connections 
            ServicePointManager.DefaultConnectionLimit = 12;

            // Do other silo initialization – for example: Azure diagnostics, etc
            
            // For information on handling configuration changes
            // see the MSDN topic at http://go.microsoft.com/fwlink/?LinkId=166357.
            return base.OnStart();
        }

        public override void OnStop()
        {
            silo.Stop();
            base.OnStop();
        }
        
        public override void Run()
        {
            var config = AzureSilo.DefaultConfiguration();
            
            // It is IMPORTANT to start the silo not in OnStart but in Run. 
            // Azure may not have the firewalls open yet (on the remote silos) at the OnStart phase.
            silo = new AzureSilo();
            bool isSiloStarted = silo.Start(config);
            
            silo.Run(); // Call will block until silo is shutdown
        } 
    }
}
