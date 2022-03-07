using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using VotingWeb.Models;

namespace VotingWeb.Controllers;

[Route("")]
[Route("Home")]
[Route("Home/Index")]
public class HomeController : Controller
{
    private readonly ILogger _logger;

    public HomeController(ILogger<HomeController> logger)
    {
        _logger = logger;
    }

    public ActionResult Index()
    {
        _logger.LogInformation("Returning Index page");
        return View();
    }

    [Route("Home/Error")]
    public ActionResult Error()
    {
        _logger.LogInformation("Returning Error page");
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
