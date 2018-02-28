using OrleansXO.GrainInterfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using Orleans;

namespace OrleansXO.Web.Controllers
{
    public class HomeController : Controller
    {

        private Guid GetGuid()
        {
            if (this.Session["playerId"] != null)
            {
                return (Guid)this.Session["playerId"];
            }
            var guid = Guid.NewGuid();
            this.Session["playerId"] = guid;
            return guid;
        }

        public class ViewModel 
        {
            public string GameId { get; set; }
        }

        public ActionResult Index(Guid? id)
        {
            var vm = new ViewModel();
            vm.GameId = (id.HasValue) ? id.Value.ToString() : "";
            return View(vm);
        }

        public async Task<ActionResult> Join(Guid id)
        {
            var guid = GetGuid();
            var player = GrainClient.GrainFactory.GetGrain<IPlayerGrain>(guid);
            var state = await player.JoinGame(id);
            return RedirectToAction("Index", id);
        }
    }
}
