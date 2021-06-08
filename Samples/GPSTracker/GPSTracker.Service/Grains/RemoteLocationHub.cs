using System.Threading;
using System.Threading.Tasks;
using GPSTracker.Common;
using Microsoft.AspNetCore.SignalR;

namespace GPSTracker
{
    /// <summary>
    /// Broadcasts location messages to clients which are connected to the local SignalR hub.
    /// </summary>
    internal class RemoteLocationHub : IRemoteLocationHub
    {
        private readonly IHubContext<LocationHub> _hub;
        public RemoteLocationHub(IHubContext<LocationHub> hub) => _hub = hub;

        // Send a mesage to every client which is connected to the hub
        public async Task BroadcastUpdates(VelocityBatch messages) => await _hub.Clients.All.SendAsync("locationUpdates", messages, CancellationToken.None);
    }
}
