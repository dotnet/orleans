using Orleans;
using Orleans.Runtime;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GPSTracker.GrainImplementation
{
    /// <summary>
    /// Service discovery for the active SignalR hubs.
    /// </summary>
    public interface IHubListGrain : IGrainWithGuidKey
    {
        ValueTask AddHub(SiloAddress host, IRemoteLocationHub hubReference);
        ValueTask<List<(SiloAddress Host, IRemoteLocationHub Hub)>> GetHubs(); 
    }
}
