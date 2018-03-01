using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GrainInterfaces;
using Microsoft.AspNetCore.Mvc;
using Orleans;

namespace API.Controllers
{
    [Route("api/[controller]")]
    public class ValuesController : Controller
    {
        private IClusterClient client;
        
        public ValuesController(IClusterClient client)
        {
            this.client = client;
        }

        // GET api/values
        [HttpGet]
        public IEnumerable<string> Get()
        {
            return new string[] { "value1", "value2" };
        }

        // GET api/values/5
        [HttpGet("{id}")]
        public async Task<string> Get(int id)
        {
            var grain = this.client.GetGrain<IValueGrain>(id);
            return await grain.GetValue();
        }

        // PUT api/values/5
        [HttpPost("{id}")]
        public async Task Post(int id, [FromBody]string value)
        {
            var grain = this.client.GetGrain<IValueGrain>(id);
            await grain.SetValue(value);
        }
    }
}
