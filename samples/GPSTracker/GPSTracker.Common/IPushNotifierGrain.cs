using GPSTracker.Common;

namespace GPSTracker.GrainInterface;

public interface IPushNotifierGrain : IGrainWithIntegerKey
{
    Task SendMessage(VelocityMessage message);
}
