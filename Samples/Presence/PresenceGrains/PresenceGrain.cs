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
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Samples.Presence.GrainInterfaces;

namespace PresenceGrains
{
    /// <summary>
    /// Stateless grain that decodes binary blobs and routes then to the appropriate game grains based on the blob content.
    /// Simulates how a cloud service receives raw data from a device and needs to preprocess it before forwarding for the actial computation.
    /// </summary>
    [StatelessWorker]
    public class PresenceGrain : Grain, IPresenceGrain
    {
        public Task Heartbeat(byte[] data)
        {
            HeartbeatData heartbeatData = HeartbeatDataDotNetSerializer.Deserialize(data);
            IGameGrain game = GameGrainFactory.GetGrain(heartbeatData.Game);
            return game.UpdateGameStatus(heartbeatData.Status);
        }
    }
}
