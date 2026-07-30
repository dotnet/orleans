using GPSTracker.Common;
using GPSTracker.GrainInterface;
using Orleans.Concurrency;

namespace GPSTracker.GrainImplementation;

[Reentrant]
public class DeviceGrain : Grain, IDeviceGrain
{
    private DeviceMessage _lastMessage = null!;

    private readonly IPushNotifierGrain _pushNotifier;

    public DeviceGrain() =>
        _pushNotifier = GrainFactory.GetGrain<IPushNotifierGrain>(0);

    public async ValueTask ProcessMessage(DeviceMessage message)
    {
        if (_lastMessage is null || _lastMessage.Latitude != message.Latitude || _lastMessage.Longitude != message.Longitude)
        {
            // Only sent a notification if the position has changed
            double speed = GetSpeed(_lastMessage, message);

            // Record the last message
            _lastMessage = message;

            // Forward the message to the notifier grain
            var velocityMessage = new VelocityMessage(message, speed);
            await _pushNotifier.SendMessage(velocityMessage);
        }
        else
        {
            // The position has not changed, just record the last message
            _lastMessage = message;
        }
    }

    private static double GetSpeed(DeviceMessage? message1, DeviceMessage message2)
    {
        // Calculate the speed of the device, using the internal state of the grain
        if (message1 is null || message2 is null)
        {
            return 0;
        }

        const double R = 6_371 * 1_000;
        double x = (message2.Longitude - message1.Longitude) * Math.Cos((message2.Latitude + message1.Latitude) / 2);
        double y = message2.Latitude - message1.Latitude;
        double distance = Math.Sqrt(Math.Pow(x, 2) + Math.Pow(y, 2)) * R;
        double time = (message2.Timestamp - message1.Timestamp).TotalSeconds;
        return time switch
        {
            0 => 0,
            _ => distance / time,
        };
    }
}
