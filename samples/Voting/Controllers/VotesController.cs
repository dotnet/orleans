using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Orleans;
using VotingContract;

namespace VotingWeb.Controllers;

[Route("api/[controller]")]
public class VotesController : Controller
{
    private readonly IVoteGrain _voteGrain;
    private readonly ILogger _logger;

    public VotesController(IClusterClient client, ILogger<VotesController> logger)
    {
        _voteGrain = client.GetGrain<IVoteGrain>(0);
        _logger = logger;
    }

    // GET api/votes
    [HttpGet]
    public async Task<ActionResult> Get()
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Getting votes");
       
        var result = await _voteGrain.Get();

        _logger.LogInformation(
            "Returning votes in {ElapsedMilliseconds}ms", stopwatch.ElapsedMilliseconds);

        return Json(result);
    }

    // PUT api/votes/name
    [HttpPut("{name}")]
    public async Task<ActionResult> Put(string name)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Adding vote");

        await _voteGrain.AddVote(name);
        _logger.LogInformation(
            "Added vote in {ElapsedMilliseconds}ms", stopwatch.ElapsedMilliseconds);

        return Ok();
    }

    // DELETE api/votes/name
    [HttpDelete("{name}")]
    public async Task<ActionResult> Delete(string name)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Removing vote");

        await _voteGrain.RemoveVote(name);
        _logger.LogInformation(
            "Removed vote in {ElapsedMilliseconds}ms", stopwatch.ElapsedMilliseconds);

        return Ok();
    }
}
