using Microsoft.AspNetCore.Mvc;
using Orleans.Runtime;
using Orleans;

namespace ActivationRebalancing.Frontend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StatsController(IClusterClient clusterClient) : ControllerBase
{
    [HttpGet("silos")]
    public async Task<IActionResult> GetStats()
    {
        var grainStats = await clusterClient
            .GetGrain<IManagementGrain>(0)
            .GetDetailedGrainStatistics();

        var siloData = grainStats.GroupBy(stat => stat.SiloAddress)
            .Select(g => new SiloData(g.Key.ToString(), g.Count()))
            .ToList();

        if (siloData.Count == 4)
        {
            siloData = [.. siloData, new SiloData("x", 0)];
        }

        if (siloData.Count > 5)
        {
            throw new NotSupportedException("The frontend cant support more than 6 silos");
        }

        return Ok(siloData);
    }
}

public record SiloData(string Host, int Activations);