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

            return silo.Start(RoleEnvironment.DeploymentId, RoleEnvironment.CurrentRoleInstance);
        }

        public override void OnStop() { silo.Stop(); }
        public override void Run() { silo.Run(); }
    }
}