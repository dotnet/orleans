using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Dashboard.Metrics.Details;
using Orleans.Dashboard.Model;

namespace Orleans.Dashboard.Implementation.Details;

internal sealed class SiloStatusOracleSiloDetailsProvider(ISiloStatusOracle siloStatusOracle) : ISiloDetailsProvider
{
    public Task<SiloDetails[]> GetSiloDetails()
    {
        return Task.FromResult(siloStatusOracle.GetApproximateSiloStatuses(true)
            .Select(x => new SiloDetails
            {
                Status = x.Value.ToString(),
                SiloStatus = x.Value,
                SiloAddress = x.Key.ToParsableString(),
                SiloName = x.Key.ToParsableString() // Use the address for naming
            })
            .ToArray());
    }
}
