using GPSTracker.Common;

namespace GPSTracker.GrainInterface;

public interface IDeviceGrain : IGrainWithIntegerKey
{
    Task ProcessMessage(DeviceMessage message);
}
