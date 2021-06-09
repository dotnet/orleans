using System;
using System.Threading.Tasks;
using Orleans;
using Microsoft.AspNetCore.Mvc;
using TicTacToe.Grains;

namespace TicTacToe.Controllers
{
    public class GameController : Controller
    {
        private readonly IGrainFactory _grainFactory;
        public GameController(IGrainFactory grainFactory) => _grainFactory = grainFactory;
        public async Task<IActionResult> Index()
        {
            var guid = this.GetGuid();
            var player = _grainFactory.GetGrain<IPlayerGrain>(guid);
            var gamesTask = player.GetGameSummaries();
            var availableTask = player.GetAvailableGames();
            await Task.WhenAll(gamesTask, availableTask);

            return Json(new object[] { gamesTask.Result, availableTask.Result });
        }

        public async Task<IActionResult> CreateGame()
        {
            var guid = this.GetGuid();
            var player = _grainFactory.GetGrain<IPlayerGrain>(guid);
            var gameIdTask = await player.CreateGame();
            return Json(new { GameId = gameIdTask });
        }

        public async Task<IActionResult> Join(Guid id)
        {
            var player = _grainFactory.GetGrain<IPlayerGrain>(this.GetGuid());
            var state = await player.JoinGame(id);
            return Json(new { GameState = state });
        }

        public async Task<IActionResult> GetMoves(Guid id)
        {
            var game = _grainFactory.GetGrain<IGameGrain>(id);
            var moves = await game.GetMoves();
            var summary = await game.GetSummary(this.GetGuid());
            return Json(new { moves = moves, summary = summary });
        }

        [HttpPost]
        public async Task<IActionResult> MakeMove(Guid id, int x, int y)
        {
            var game = _grainFactory.GetGrain<IGameGrain>(id);
            var move = new GameMove { PlayerId = this.GetGuid(), X = x, Y = y };
            var state = await game.MakeMove(move);
            return Json(state);
        }

        public async Task<IActionResult> QueryGame(Guid id)
        {
            var game = _grainFactory.GetGrain<IGameGrain>(id);
            var state = await game.GetState();
            return Json(state);

        }

        [HttpPost]
        public async Task<IActionResult> SetUser(string id)
        {
            var player = _grainFactory.GetGrain<IPlayerGrain>(this.GetGuid());
            await player.SetUsername(id);
            return Json(new { });
        }
    }
}
