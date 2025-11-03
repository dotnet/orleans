using System.Threading.Tasks;
using Orleans.Dashboard.Model;

namespace Orleans.Dashboard.Metrics.Details;

internal interface ISiloDetailsProvider
{
    Task<SiloDetails[]> GetSiloDetails();
}
