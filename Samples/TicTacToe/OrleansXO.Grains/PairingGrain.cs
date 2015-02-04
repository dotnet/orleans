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
using System.Linq;
using System.Runtime.Caching;
using System.Threading.Tasks;

using Orleans;
using Orleans.Concurrency;
using OrleansXO.GrainInterfaces;

namespace OrleansXO.Grains
{
    /// <summary>
    /// Orleans grain implementation class GameGrain
    /// </summary>
    [Reentrant]
    public class PairingGrain : Grain, IPairingGrain
    {
        MemoryCache cache;

        public override Task OnActivateAsync()
        {
            cache = new MemoryCache("pairing");
            return base.OnActivateAsync();
        }

        public Task AddGame(Guid gameId, string name)
        {
            cache.Add(gameId.ToString(), name, new DateTimeOffset(DateTime.UtcNow).AddHours(1));
            return TaskDone.Done;
        }

        public Task RemoveGame(Guid gameId)
        {
            cache.Remove(gameId.ToString());
            return TaskDone.Done;
        }

        public Task<PairingSummary[]> GetGames()
        {
            return Task.FromResult(this.cache.Select(x => new PairingSummary { GameId = Guid.Parse(x.Key), Name = x.Value as string }).ToArray());
        }

    }
}
