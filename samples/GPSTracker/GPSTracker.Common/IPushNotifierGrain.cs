using GPSTracker.Common;
using Orleans;

namespace GPSTracker.GrainInterface;

public interface IPushNotifierGrain : IGrainWithIntegerKey
{
    Task SendMessage(VelocityMessage message);
}
