using System;
using System.Threading.Tasks;
using AspNetCoreHostedServices.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Orleans;

namespace ASPNetCoreHostedServices.Controllers
{
    [ApiController]
    [Route("api/hello")]
    public class HelloWorldController : ControllerBase
    {
        private readonly IClusterClient _client;
        private readonly IHelloWorld _grain;

        public HelloWorldController(IClusterClient client) {
            this._client = client;
            this._grain = this._client.GetGrain<IHelloWorld>(0);
        }

        [HttpGet]
        public Task<string> SayHello() => this._grain.SayHello();
    }
}