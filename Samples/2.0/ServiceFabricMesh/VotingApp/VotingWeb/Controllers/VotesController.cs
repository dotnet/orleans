using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Orleans;
using VotingContract;

namespace VotingWeb.Controllers
{
    [Route("api/[controller]")]
    public class VotesController : Controller
    {
        private readonly IVoteGrain voteGrain;
        private readonly ILogger logger;

        public VotesController(IClusterClient client, ILogger<VotesController> logger)
        {
            this.voteGrain = client.GetGrain<IVoteGrain>(0);
            this.logger = logger;
        }

        // GET api/votes
        [HttpGet]
        public async Task<IActionResult> Get()
        {
            var stopwatch = Stopwatch.StartNew();
            this.logger.LogInformation("Getting votes");
           
            var result = await this.voteGrain.Get();

            this.logger.LogInformation($"Returning votes in {stopwatch.ElapsedMilliseconds}ms");

            return this.Json(result);
        }

        // PUT api/votes/name
        [HttpPut("{name}")]
        public async Task<IActionResult> Put(string name)
        {
            var stopwatch = Stopwatch.StartNew();
            this.logger.LogInformation("Adding vote");

            await this.voteGrain.AddVote(name);
            this.logger.LogInformation($"Added vote in {stopwatch.ElapsedMilliseconds}ms");

            return Ok();
        }

        // DELETE api/votes/name
        [HttpDelete("{name}")]
        public async Task<IActionResult> Delete(string name)
        {
            var stopwatch = Stopwatch.StartNew();
            this.logger.LogInformation("Removing vote");

            await this.voteGrain.RemoveVote(name);
            this.logger.LogInformation($"Removed vote in {stopwatch.ElapsedMilliseconds}ms");

            return Ok();
        }
    }
}
