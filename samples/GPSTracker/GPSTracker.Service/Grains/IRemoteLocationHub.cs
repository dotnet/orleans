using GPSTracker.Common;
using Orleans;

namespace GPSTracker;

public interface IRemoteLocationHub : IGrainObserver
{
    Task BroadcastUpdates(VelocityBatch messages);
}
