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
    public interface IGameGrain : Orleans.IGrain
    {
        // add a player into a game
        Task<GameState> AddPlayerToGame(Guid player);
        Task<GameState> GetState();
        Task<List<GameMove>> GetMoves();
        Task<GameState> MakeMove(GameMove move);
        Task<GameSummary> GetSummary(Guid player);
        Task SetName(string name);
    }


    // define the possible states a game can be in
    public enum GameState
    {
        AwaitingPlayers,
        InPlay,
        Finished
    }


    // define game outcomes
    public enum GameOutcome
    {
        Win,
        Lose,
        Draw
    }


    public struct GameMove
    {
        public Guid PlayerId { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
    }

    public struct GameSummary
    {
        public GameState State { get; set; }
        public bool YourMove { get; set; }
        public int NumMoves { get; set; }
        public GameOutcome Outcome { get; set; }
        public int NumPlayers { get; set; }
        public Guid GameId { get; set; }
        public string[] Usernames { get; set; }
        public string Name { get; set; }
        public bool GameStarter { get; set; }
    }
}
