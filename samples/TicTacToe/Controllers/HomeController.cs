using Microsoft.AspNetCore.Mvc;
using Orleans;
using System;
using System.Threading.Tasks;
using TicTacToe.Grains;

namespace TicTacToe.Controllers
{
    public class ViewModel 
    {
        public string GameId { get; set; }
    }

    public class HomeController : Controller
    {
        private readonly IGrainFactory _grainFactory;
        public HomeController(IGrainFactory grainFactory) => _grainFactory = grainFactory;

        public IActionResult Index(Guid? id)
        {
            var vm = new ViewModel
            {
                GameId = id.HasValue ? id.Value.ToString() : ""
            };

            return View("Views/Index.cshtml", vm);
        }

        public async Task<IActionResult> Join(Guid id)
        {
            var guid = this.GetGuid();
            var player = _grainFactory.GetGrain<IPlayerGrain>(guid);
            await player.JoinGame(id);
            return RedirectToAction("Index", id);
        }
    }
}
