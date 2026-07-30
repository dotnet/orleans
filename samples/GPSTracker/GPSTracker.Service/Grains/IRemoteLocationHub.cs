using GPSTracker.Common;

namespace GPSTracker;

public interface IRemoteLocationHub : IGrainObserver
{
    ValueTask BroadcastUpdates(VelocityBatch messages);
}
