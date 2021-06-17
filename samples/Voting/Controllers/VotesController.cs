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
            voteGrain = client.GetGrain<IVoteGrain>(0);
            this.logger = logger;
        }

        // GET api/votes
        [HttpGet]
        public async Task<ActionResult> Get()
        {
            var stopwatch = Stopwatch.StartNew();
            logger.LogInformation("Getting votes");
           
            var result = await voteGrain.Get();

            logger.LogInformation($"Returning votes in {stopwatch.ElapsedMilliseconds}ms");

            return Json(result);
        }

        // PUT api/votes/name
        [HttpPut("{name}")]
        public async Task<ActionResult> Put(string name)
        {
            var stopwatch = Stopwatch.StartNew();
            logger.LogInformation("Adding vote");

            await voteGrain.AddVote(name);
            logger.LogInformation($"Added vote in {stopwatch.ElapsedMilliseconds}ms");

            return Ok();
        }

        // DELETE api/votes/name
        [HttpDelete("{name}")]
        public async Task<ActionResult> Delete(string name)
        {
            var stopwatch = Stopwatch.StartNew();
            logger.LogInformation("Removing vote");

            await voteGrain.RemoveVote(name);
            logger.LogInformation($"Removed vote in {stopwatch.ElapsedMilliseconds}ms");

            return Ok();
        }
    }
}
