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

using OrleansXO.GrainInterfaces;
using System;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;

namespace OrleansXO.Web.Controllers
{
    public class GameController : Controller
    {
        private Guid GetGuid()
        {
            if (this.Request.Cookies["playerId"] != null)
            {
                return Guid.Parse(this.Request.Cookies["playerId"].Value);
            }
            var guid = Guid.NewGuid();
            this.Response.Cookies.Add(new HttpCookie("playerId", guid.ToString()));
            return guid;
        }

        public async Task<ActionResult> Index()
        {
            var guid = GetGuid();
            var player = PlayerGrainFactory.GetGrain(guid);
            var gamesTask = player.GetGameSummaries();
            var availableTask = player.GetAvailableGames();
            await Task.WhenAll(gamesTask, availableTask);

            return Json(new object[] { gamesTask.Result, availableTask.Result }, JsonRequestBehavior.AllowGet);
        }

        public async Task<ActionResult> CreateGame()
        {
            var guid = GetGuid();
            var player = PlayerGrainFactory.GetGrain(guid);
            var gameIdTask = await player.CreateGame();
            return Json(new { GameId = gameIdTask }, JsonRequestBehavior.AllowGet);
        }

        public async Task<ActionResult> Join(Guid id)
        {
            var guid = GetGuid();
            var player = PlayerGrainFactory.GetGrain(guid);
            var state = await player.JoinGame(id);
            return Json(new { GameState = state }, JsonRequestBehavior.AllowGet);
        }

        public async Task<ActionResult> GetMoves(Guid id)
        {
            var guid = GetGuid();
            var game = GameGrainFactory.GetGrain(id);
            var moves = await game.GetMoves();
            var summary = await game.GetSummary(guid);
            return Json(new { moves = moves, summary = summary }, JsonRequestBehavior.AllowGet);
        }

        [HttpPost]
        public async Task<ActionResult> MakeMove(Guid id, int x, int y)
        {
            var guid = GetGuid();
            var game = GameGrainFactory.GetGrain(id);
            var move = new GameMove { PlayerId = guid, X = x, Y = y };
            var state = await game.MakeMove(move);
            return Json(state, JsonRequestBehavior.AllowGet);
        }

        public async Task<ActionResult> QueryGame(Guid id)
        {
            var game = GameGrainFactory.GetGrain(id);
            var state = await game.GetState();
            return Json(state, JsonRequestBehavior.AllowGet);

        }

        [HttpPost]
        public async Task<ActionResult> SetUser(string id)
        {
            var guid = GetGuid();
            var player = PlayerGrainFactory.GetGrain(guid);
            await player.SetUsername(id);
            return Json(new { });
        }




    }
}