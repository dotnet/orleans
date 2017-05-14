using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Orleans;

namespace OrleansWebAPIClient.Controllers
{
    [Route("api/[controller]")]
    public class GreetingsController : Controller
    {
        private IClusterClient _clusterClient;

        public GreetingsController(IClusterClient clusterClient)
        {
            _clusterClient = clusterClient;
        }

        [HttpGet]
        public Task<string> Greet(string greet)
        {
            var grain = _clusterClient.GetGrain<IHellogGrain>(Guid.NewGuid());

            return grain.SayHello("Hello Gutemberg!");
        }
    }
}
