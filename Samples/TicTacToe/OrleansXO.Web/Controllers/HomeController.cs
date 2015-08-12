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