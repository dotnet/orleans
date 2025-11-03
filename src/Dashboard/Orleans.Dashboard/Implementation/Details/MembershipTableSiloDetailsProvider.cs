using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Dashboard.Metrics.Details;
using Orleans.Dashboard.Model;
using Orleans.Dashboard.Core;

namespace Orleans.Dashboard.Implementation.Details;

internal sealed class MembershipTableSiloDetailsProvider : ISiloDetailsProvider
{
    private readonly IGrainFactory grainFactory;

    public MembershipTableSiloDetailsProvider(IGrainFactory grainFactory)
    {
        this.grainFactory = grainFactory;
    }

    public async Task<SiloDetails[]> GetSiloDetails()
    {
        //default implementation uses managementgrain details
        var grain = grainFactory.GetGrain<IManagementGrain>(0);

        var hosts = await grain.GetDetailedHosts(true);

        return hosts.Select(x => new SiloDetails
        {
            FaultZone = x.FaultZone,
            HostName = x.HostName,
            IAmAliveTime = x.IAmAliveTime.ToISOString(),
            ProxyPort = x.ProxyPort,
            RoleName = x.RoleName,
            SiloAddress = x.SiloAddress.ToParsableString(),
            SiloName = x.SiloName,
            StartTime = x.StartTime.ToISOString(),
            Status = x.Status.ToString(),
            SiloStatus = x.Status,
            UpdateZone = x.UpdateZone
        }).ToArray();
    }
}
