//*********************************************************//
//    Copyright (c) Microsoft. All rights reserved.
//    
//    Apache 2.0 License
//    
//    You may obtain a copy of the License at
//    http://www.apache.org/licenses/LICENSE-2.0
//    
//    Unless required by applicable law or agreed to in writing, software 
//    distributed under the License is distributed on an "AS IS" BASIS, 
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or 
//    implied. See the License for the specific language governing 
//    permissions and limitations under the License.
//
//*********************************************************

using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.ServiceRuntime;
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

            // Set the maximum number of concurrent connections 
            ServicePointManager.DefaultConnectionLimit = 12;

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

            // It is IMPORTANT to start the silo not in OnStart but in Run.
            // Azure may not have the firewalls open yet (on the remote silos) at the OnStart phase.
            orleansAzureSilo = new AzureSilo();
            bool ok = orleansAzureSilo.Start(RoleEnvironment.DeploymentId, RoleEnvironment.CurrentRoleInstance);

            Trace.WriteLine("OrleansAzureSilos-OnStart Orleans silo started ok=" + ok, "Information");

            orleansAzureSilo.Run(); // Call will block until silo is shutdown
        }

        public override void OnStop()
        {
            Trace.WriteLine("OrleansAzureSilos-OnStop called", "Information");
            if (orleansAzureSilo != null)
            {
                orleansAzureSilo.Stop();
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
                Trace.WriteLine(string.Format("RoleEnvironmentChanging: #{0} Type={1} Change={2}", i++, c.GetType().FullName, c));
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

            #region Setup CloudStorageAccount Configuration Setting Publisher
            // From: http://www.tsjensen.com/blog/2009/11/30/Windows+Azure+10+CloudTableClient+Minimal+Configuration.aspx
            // This code sets up a handler to update CloudStorageAccount instances when their corresponding
            // configuration settings change in the service configuration file.
            CloudStorageAccount.SetConfigurationSettingPublisher((string configName, Func<string, bool> configSetter) =>
            {
                // Provide the configSetter with the initial value
                configSetter(RoleEnvironment.GetConfigurationSettingValue(configName));

                RoleEnvironment.Changed += (object sender, RoleEnvironmentChangedEventArgs e) =>
                {
                    if (e.Changes.OfType<RoleEnvironmentConfigurationSettingChange>()
                        .Any((RoleEnvironmentConfigurationSettingChange change) => (change.ConfigurationSettingName == configName)))
                    {
                        // The corresponding configuration setting has changed, propagate the value
                        if (!configSetter(RoleEnvironment.GetConfigurationSettingValue(configName)))
                        {
                            // In this case, the change to the storage account credentials in the
                            // service configuration is significant enough that the role needs to be
                            // recycled in order to use the latest settings. (for example, the 
                            // endpoint has changed)
                            RoleEnvironment.RequestRecycle();
                        }
                    }
                };
            });
            #endregion
        }
    }
}
