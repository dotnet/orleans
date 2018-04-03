using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using Microsoft.Azure;
using Microsoft.WindowsAzure.ServiceRuntime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;

namespace Orleans.Azure.Silos
{
    public class WorkerRole : RoleEntryPoint
    {
        private AzureSilo orleansAzureSilo;

        public WorkerRole()
        {
            Console.WriteLine("OrleansAzureSilos-Constructor called");
        }

        public override bool OnStart()
        {
            Trace.WriteLine("OrleansAzureSilos-OnStart called", "Information");

            Trace.WriteLine("OrleansAzureSilos-OnStart Initializing config", "Information");

            // For information on handling configuration changes see the MSDN topic at http://go.microsoft.com/fwlink/?LinkId=166357.
            RoleEnvironment.Changing += RoleEnvironmentChanging;
            SetupEnvironmentChangeHandlers();

            bool ok = base.OnStart();

            Trace.WriteLine("OrleansAzureSilos-OnStart called base.OnStart ok=" + ok, "Information");

            return ok;
        }

        public override void Run()
        {
            Trace.WriteLine("OrleansAzureSilos-Run entry point called", "Information");

            Trace.WriteLine("OrleansAzureSilos-OnStart Starting Orleans silo", "Information");

            var config = new ClusterConfiguration();

            config.LoadFromFile("OrleansConfiguration.xml");

            // check if the user forgot to change Orleans.xml before deploying to cloud
            if (!CloudConfigurationManager.GetSetting("DataConnectionString").Contains("UseDevelopmentStorage")
                && config.Globals
                    .ProviderConfigurations[ProviderCategoryConfiguration.STORAGE_PROVIDER_CATEGORY_NAME]
                    .Providers["GloballySharedAzureAccount"]
                    .Properties["DataConnectionString"]
                    .Contains("UseDevelopmentStorage"))
                throw new Exception(
                    "please edit OrleansConfiguration.xml to configure global azure storage account before deploying to cloud");

            // insert the ClusterId based on the cloud configuration
            config.Globals.ClusterId = CloudConfigurationManager.GetSetting("ClusterId");


            // It is IMPORTANT to start the silo not in OnStart but in Run.
            // Azure may not have the firewalls open yet (on the remote silos) at the OnStart phase.
            this.orleansAzureSilo = new AzureSilo();
            bool ok = this.orleansAzureSilo.Start(config);

            Trace.WriteLine("OrleansAzureSilos-OnStart Orleans silo started ok=" + ok, "Information");

            this.orleansAzureSilo.Run(); // Call will block until silo is shutdown
        }

        public override void OnStop()
        {
            Trace.WriteLine("OrleansAzureSilos-OnStop called", "Information");
            if (this.orleansAzureSilo != null)
            {
                this.orleansAzureSilo.Stop();
            }
            RoleEnvironment.Changing -= RoleEnvironmentChanging;
            base.OnStop();
            Trace.WriteLine("OrleansAzureSilos-OnStop finished", "Information");
        }

        private static void RoleEnvironmentChanging(object sender, RoleEnvironmentChangingEventArgs e)
        {
            int i = 1;
            foreach (var c in e.Changes)
            {
                Trace.WriteLine(string.Format("RoleEnvironmentChanging: #{0} Type={1} Change={2}", i++,
                    c.GetType().FullName, c));
            }

            // If a configuration setting is changing);
            if (e.Changes.Any((RoleEnvironmentChange change) => change is RoleEnvironmentConfigurationSettingChange))
            {
                // Set e.Cancel to true to restart this role instance
                e.Cancel = true;
            }
        }

        private static void SetupEnvironmentChangeHandlers()
        {
            // For information on handling configuration changes see the MSDN topic at http://go.microsoft.com/fwlink/?LinkId=166357.
        }
    }
}