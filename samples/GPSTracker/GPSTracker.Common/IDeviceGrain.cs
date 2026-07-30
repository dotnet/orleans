using GPSTracker.Common;

namespace GPSTracker.GrainInterface;

public interface IDeviceGrain : IGrainWithIntegerKey
{
    ValueTask ProcessMessage(DeviceMessage message);
}
