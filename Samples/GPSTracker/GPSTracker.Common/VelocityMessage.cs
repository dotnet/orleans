using System;
using Orleans.Concurrency;

namespace GPSTracker.Common
{
    [Immutable]
    [Serializable]
    public class VelocityMessage : DeviceMessage
    {
        public VelocityMessage()
        { }

        public VelocityMessage(DeviceMessage deviceMessage, double velocity)
        {
            Latitude = deviceMessage.Latitude;
            Longitude = deviceMessage.Longitude;
            MessageId = deviceMessage.MessageId;
            DeviceId = deviceMessage.DeviceId;
            Timestamp = deviceMessage.Timestamp;
            Velocity = velocity;
        }

        public double Velocity { get; set; }
    }

    [Immutable]
    [Serializable]
    public class VelocityBatch
    {
        public VelocityMessage[] Messages { get; set; }
    }
}
