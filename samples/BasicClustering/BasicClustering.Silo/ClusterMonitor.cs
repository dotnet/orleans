using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace BasicClustering;

public sealed partial class ClusterMonitor(
    IClusterMembershipService membership,
    IGrainFactory grainFactory,
    ILogger<ClusterMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var grainCalled = false;

        await foreach (var snapshot in membership.MembershipUpdates.WithCancellation(stoppingToken))
        {
            var activeMembers = snapshot.Members.Values
                .Where(static member => member.Status is SiloStatus.Active)
                .OrderBy(static member => member.Name)
                .ToArray();

            var memberList = string.Join(
                ", ",
                activeMembers.Select(static member => $"{member.Name} at {member.SiloAddress}"));
            LogClusterView(logger, activeMembers.Length, memberList);

            if (activeMembers.Length < 2 || grainCalled)
            {
                continue;
            }

            var grain = grainFactory.GetGrain<IHelloGrain>(0);
            var response = await grain.SayHello("Hello from the cluster");
            LogGrainResponse(logger, response);
            grainCalled = true;
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Observed {ActiveSiloCount} active silo(s): {ActiveSilos}")]
    private static partial void LogClusterView(
        ILogger logger,
        int activeSiloCount,
        string activeSilos);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "The two-silo cluster is ready. {GrainResponse}")]
    private static partial void LogGrainResponse(ILogger logger, string grainResponse);
}
