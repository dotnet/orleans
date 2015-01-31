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
using Microsoft.WindowsAzure;
//using Microsoft.WindowsAzure.Diagnostics;
using Microsoft.WindowsAzure.ServiceRuntime;

namespace Orleans.Azure.Samples.Web
{
    public class WebRole : RoleEntryPoint
    {
        public override bool OnStart()
        {
            Trace.WriteLine("OrleansAzureWeb-OnStart");

            // For information on handling configuration changes see the MSDN topic at http://go.microsoft.com/fwlink/?LinkId=166357.
            RoleEnvironment.Changing += RoleEnvironmentChanging;
            
            #region Setup CloudStorageAccount Configuration Setting Publisher

            // This code sets up a handler to update CloudStorageAccount instances when their corresponding
            // configuration settings change in the service configuration file.
            CloudStorageAccount.SetConfigurationSettingPublisher(
                (string configName, Func<string, bool> configSetter) =>
                {
                    // Provide the configSetter with the initial value
                    configSetter(RoleEnvironment.GetConfigurationSettingValue(configName));

                    RoleEnvironment.Changed += (sender, arg) =>
                    {
                        if (arg.Changes.OfType<RoleEnvironmentConfigurationSettingChange>()
                            .Any((change) => (change.ConfigurationSettingName == configName)))
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

            bool ok = base.OnStart();

            Trace.WriteLine("OrleansAzureWeb-OnStart completed with OK=" + ok);

            return ok;
        }

        public override void OnStop()
        {
            Trace.WriteLine("OrleansAzureWeb-OnStop");
            base.OnStop();
        }

        public override void Run()
        {
            Trace.WriteLine("OrleansAzureWeb-Run");
            try
            {
                base.Run();
            }
            catch (Exception exc)
            {
                Trace.WriteLine("Run() failed with " + exc.ToString());
            }
        }

        private void RoleEnvironmentChanging(object sender, RoleEnvironmentChangingEventArgs e)
        {
            // If a configuration setting is changing
            if (e.Changes.Any(change => change is RoleEnvironmentConfigurationSettingChange))
            {
                // Set e.Cancel to true to restart this role instance
                e.Cancel = true;
            }
        }
    }
}
