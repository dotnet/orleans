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
using System.Threading.Tasks;
using System.Text;
using Orleans;

namespace OrleansXO.GrainInterfaces
{
    public interface IPlayerGrain : IGrain
    {
        // get a list of all active games

        Task<PairingSummary[]> GetAvailableGames();

        Task<List<GameSummary>> GetGameSummaries();

        // create a new game and join it
        Task<Guid> CreateGame();

        // join an existing game
        Task<GameState> JoinGame(Guid gameId);

        Task LeaveGame(Guid gameId, GameOutcome outcome);

        Task SetUsername(string username);

        Task<string> GetUsername();
    }
}
