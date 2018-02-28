using System.Threading.Tasks;
using GPSTracker.Common;
using Orleans;

namespace GPSTracker.GrainInterface
{
    public interface IDeviceGrain : IGrainWithIntegerKey
    {
        Task ProcessMessage(DeviceMessage message);
    }
}
