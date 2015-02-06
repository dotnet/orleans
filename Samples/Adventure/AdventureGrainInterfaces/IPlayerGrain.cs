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
    /// <summary>
    /// A player is, well, there's really no other good name...
    /// </summary>
    public interface IPlayerGrain : Orleans.IGrainWithGuidKey
    {
        // Players have names
        Task<string> Name();
        Task SetName(string name);

        // Each player is located in exactly one room
        Task SetRoomGrain(IRoomGrain room);
        Task<IRoomGrain> RoomGrain();

        // Until Death comes knocking
        Task Die();

        // A Player takes his turn by calling Play with a command
        Task<string> Play(string command);

    }
}
