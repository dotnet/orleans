using System;
using System.Threading.Tasks;
using System.Web.Mvc;
using GPSTracker.Common;
using GPSTracker.GrainInterface;

namespace GPSTracker.Web.Controllers
{
    public class HomeController : Controller
    {
        public ActionResult Index()
        {
            return View();
        }

        public async Task<ActionResult> Test()
        {
            var rand = new Random();
            var grain = DeviceGrainFactory.GetGrain(1);
            await grain.ProcessMessage(new DeviceMessage(rand.Next(-90, 90), rand.Next(-180, 180), 1, 1, DateTime.UtcNow));
            return Content("Sent");
        }


    }
}
