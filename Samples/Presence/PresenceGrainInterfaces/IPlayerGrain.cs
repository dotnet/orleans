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

namespace Orleans.Samples.Presence.GrainInterfaces
{
    /// <summary>
    /// Interface to an individual player that may or may not be in a game at any point in time
    /// </summary>
    public interface IPlayerGrain : IGrain
    {
        Task<IGameGrain> GetCurrentGame();

        Task JoinGame(IGameGrain game);
        Task LeaveGame(IGameGrain game);
    }
}
