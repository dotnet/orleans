using System.Threading.Tasks;
using AspNetCoreCohosting.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Orleans;

namespace AspNetCoreCohosting.Controllers
{
    [ApiController]
    [Route("api/hello")]
    public class HelloWorldController : ControllerBase
    {
        private readonly IClusterClient _client;
        private readonly IHelloWorld _grain;

        public HelloWorldController(IClusterClient client)
        {
            _client = client;
            _grain = _client.GetGrain<IHelloWorld>(0);
        }

        [HttpGet]
        public Task<string> SayHello() => this._grain.SayHello();
    }
}