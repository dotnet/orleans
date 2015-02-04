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

using System.Collections.Generic;
using System.Threading.Tasks;

using Orleans;

namespace AdventureGrainInterfaces
{
    /// <summary>
    /// A room is any location in a game, including outdoor locations and
    /// spaces that are arguably better described as moist, cold, caverns.
    /// </summary>
    public interface IRoomGrain : Orleans.IGrainWithIntegerKey
    {
        // Rooms have a textual description
        Task<string> Description(PlayerInfo whoisAsking);
        Task SetInfo(RoomInfo info);

        Task<IRoomGrain> ExitTo(string direction);

        // Players can enter or exit a room
        Task Enter(PlayerInfo player);
        Task Exit(PlayerInfo player);

        // Players can enter or exit a room
        Task Enter(MonsterInfo monster);
        Task Exit(MonsterInfo monster);

        // Things can be dropped or taken from a room
        Task Drop(Thing thing);
        Task Take(Thing thing);
        Task<Thing> FindThing(string name);

        // Players and monsters can be killed, if you have the right weapon.
        Task<PlayerInfo> FindPlayer(string name);
        Task<MonsterInfo> FindMonster(string name);
    }



}
