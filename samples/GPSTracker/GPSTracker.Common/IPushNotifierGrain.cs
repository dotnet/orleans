using GPSTracker.Common;

namespace GPSTracker.GrainInterface;

public interface IPushNotifierGrain : IGrainWithIntegerKey
{
    ValueTask SendMessage(VelocityMessage message);
}
