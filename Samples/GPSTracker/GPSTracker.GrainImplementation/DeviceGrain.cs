using GPSTracker.Common;
using GPSTracker.GrainInterface;
using Orleans;
using Orleans.Concurrency;
using System;
using System.Threading.Tasks;

namespace GPSTracker.GrainImplementation
{
    /// <summary>
    /// Orleans grain implementation class.
    /// </summary>
    [Reentrant]
    public class DeviceGrain : Orleans.Grain, IDeviceGrain
    {
        public DeviceMessage LastMessage { get; set; }

        public async Task ProcessMessage(DeviceMessage message)
        {
            if (null == this.LastMessage || this.LastMessage.Latitude != message.Latitude || this.LastMessage.Longitude != message.Longitude)
            {
                // only sent a notification if the position has changed
                var notifier = GrainFactory.GetGrain<IPushNotifierGrain>(0);
                var speed = GetSpeed(this.LastMessage, message);

                // record the last message
                this.LastMessage = message;

                // forward the message to the notifier grain
                var velocityMessage = new VelocityMessage(message, speed);
                await notifier.SendMessage(velocityMessage);
            }
            else
            {
                // the position has not changed, just record the last message
                this.LastMessage = message;
            }
        }

        static double GetSpeed(DeviceMessage message1, DeviceMessage message2)
        {
            // calculate the speed of the device, using the interal state of the grain
            if (message1 == null) return 0;
            if (message2 == null) return 0;

            const double R = 6371 * 1000;
            var x = (message2.Longitude - message1.Longitude) * Math.Cos((message2.Latitude + message1.Latitude) / 2);
            var y = message2.Latitude - message1.Latitude;
            var distance = Math.Sqrt(Math.Pow(x, 2) + Math.Pow(y, 2)) * R;
            var time = (message2.Timestamp - message1.Timestamp).TotalSeconds;
            if (time == 0) return 0;
            return distance / time;
        }

    }
}
