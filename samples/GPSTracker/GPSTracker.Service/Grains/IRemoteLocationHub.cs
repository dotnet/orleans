using GPSTracker.Common;

namespace GPSTracker;

public interface IRemoteLocationHub : IGrainObserver
{
    Task BroadcastUpdates(VelocityBatch messages);
}
