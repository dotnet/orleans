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

using Orleans;
using System.Threading.Tasks;

namespace AdventureGrainInterfaces
{
    public interface IMonsterGrain : Orleans.IGrainWithIntegerKey
    {
        // Even monsters have a name
        Task<string> Name();
        Task SetInfo(MonsterInfo info);

        // Monsters are located in exactly one room
        Task SetRoomGrain(IRoomGrain room);
        Task<IRoomGrain> RoomGrain();

        Task<string> Kill(IRoomGrain room);
    }
}
