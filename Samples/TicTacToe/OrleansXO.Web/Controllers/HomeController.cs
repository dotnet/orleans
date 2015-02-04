﻿//*********************************************************//
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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;

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
            var player = PlayerGrainFactory.GetGrain(guid);
            var state = await player.JoinGame(id);
            return RedirectToAction("Index", id);
        }


    }
}