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
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;

namespace Orleans.Samples.Presence.GrainInterfaces
{
    /// <summary>
    /// Defines an interface for sending binary updates without knowing the specific game ID.
    /// Simulates what game consoles do when they send data to the cloud.
    /// </summary>
    public interface IPresenceGrain : IGrain
    {
        Task Heartbeat(byte[] data);
    }
}
