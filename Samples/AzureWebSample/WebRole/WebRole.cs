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
