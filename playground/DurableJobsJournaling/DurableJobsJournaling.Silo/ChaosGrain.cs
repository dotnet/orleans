using DurableJobsJournaling.Abstractions;

namespace DurableJobsJournaling.Silo;

public sealed class ChaosGrain(
    IClusterMembershipService clusterMembership,
    ILocalSiloDetails localSiloDetails)
    : Grain, IChaosGrain
{
    private ChaosResult? _lastResult;

    public async Task<ChaosResult> TryKillSiloAsync()
    {
        var activeSilos = clusterMembership.CurrentSnapshot.Members
            .Where(static item => item.Value.Status == SiloStatus.Active)
            .Select(static item => item.Key)
            .OrderBy(static silo => silo.ToString(), StringComparer.Ordinal)
            .ToArray();

        var target = activeSilos.FirstOrDefault(silo => !silo.Equals(localSiloDetails.SiloAddress)) ?? activeSilos.FirstOrDefault();
        if (target is null)
        {
            return _lastResult = new ChaosResult(
                Requested: true,
                Succeeded: false,
                Message: "No active silo was available to mark dead.",
                SiloAddress: null,
                DateTimeOffset.UtcNow,
                activeSilos.Select(static silo => silo.ToString()).ToArray());
        }

        var succeeded = await clusterMembership.TryKill(target);
        return _lastResult = new ChaosResult(
            Requested: true,
            Succeeded: succeeded,
            Message: succeeded
                ? $"Marked silo {target} dead through the cluster membership service."
                : $"The cluster membership service did not mark silo {target} dead.",
            SiloAddress: target.ToString(),
            DateTimeOffset.UtcNow,
            activeSilos.Select(static silo => silo.ToString()).ToArray());
    }

    public Task<ChaosResult> GetLastResultAsync()
        => Task.FromResult(_lastResult ?? new ChaosResult(false, false, "No chaos action has been requested.", null, DateTimeOffset.UtcNow, []));
}
