/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using OrleansXO.GrainInterfaces;
using System;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using Orleans;

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
            var player = GrainClient.GrainFactory.GetGrain<IPlayerGrain>(guid);
            var gamesTask = player.GetGameSummaries();
            var availableTask = player.GetAvailableGames();
            await Task.WhenAll(gamesTask, availableTask);

            return Json(new object[] { gamesTask.Result, availableTask.Result }, JsonRequestBehavior.AllowGet);
        }

        public async Task<ActionResult> CreateGame()
        {
            var guid = GetGuid();
            var player = GrainClient.GrainFactory.GetGrain<IPlayerGrain>(guid);
            var gameIdTask = await player.CreateGame();
            return Json(new { GameId = gameIdTask }, JsonRequestBehavior.AllowGet);
        }

        public async Task<ActionResult> Join(Guid id)
        {
            var guid = GetGuid();
            var player = GrainClient.GrainFactory.GetGrain<IPlayerGrain>(guid);
            var state = await player.JoinGame(id);
            return Json(new { GameState = state }, JsonRequestBehavior.AllowGet);
        }

        public async Task<ActionResult> GetMoves(Guid id)
        {
            var guid = GetGuid();
            var game = GrainClient.GrainFactory.GetGrain<IGameGrain>(id);
            var moves = await game.GetMoves();
            var summary = await game.GetSummary(guid);
            return Json(new { moves = moves, summary = summary }, JsonRequestBehavior.AllowGet);
        }

        [HttpPost]
        public async Task<ActionResult> MakeMove(Guid id, int x, int y)
        {
            var guid = GetGuid();
            var game = GrainClient.GrainFactory.GetGrain<IGameGrain>(id);
            var move = new GameMove { PlayerId = guid, X = x, Y = y };
            var state = await game.MakeMove(move);
            return Json(state, JsonRequestBehavior.AllowGet);
        }

        public async Task<ActionResult> QueryGame(Guid id)
        {
            var game = GrainClient.GrainFactory.GetGrain<IGameGrain>(id);
            var state = await game.GetState();
            return Json(state, JsonRequestBehavior.AllowGet);

        }

        [HttpPost]
        public async Task<ActionResult> SetUser(string id)
        {
            var guid = GetGuid();
            var player = GrainClient.GrainFactory.GetGrain<IPlayerGrain>(guid);
            await player.SetUsername(id);
            return Json(new { });
        }




    }
}