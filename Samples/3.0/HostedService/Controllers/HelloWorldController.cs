using Microsoft.AspNetCore.Mvc;

namespace ASPNetCoreHostedServices.Controllers
{
    public class HelloWorldController : Controller
    {
        // GET
        public IActionResult Index()
        {
            return View();
        }
    }
}