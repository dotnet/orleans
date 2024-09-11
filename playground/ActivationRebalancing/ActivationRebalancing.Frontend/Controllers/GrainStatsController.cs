using Microsoft.AspNetCore.Mvc;
using Orleans.Runtime;
using Orleans;

namespace ActivationRebalancing.Frontend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GrainStatsController(IClusterClient clusterClient) : ControllerBase
{
    [HttpGet("silo-stats")]
    public async Task<IActionResult> GetStats()
    {
        var grainStats = await clusterClient
            .GetGrain<IManagementGrain>(0)
            .GetDetailedGrainStatistics();

        var siloData = grainStats.GroupBy(stat => stat.SiloAddress)
            .Select(g => new SiloData(g.Key.ToString(), g.Count()))
            .ToList();

        return Ok(siloData);
    }
}

public record SiloData(string Host, int Activations);