using System;
namespace GPSTracker.Common
{
    public class VelocityMessage : DeviceMessage
    {

        public VelocityMessage()
        { }

        public VelocityMessage(DeviceMessage deviceMessage, double velocity)
        {
            this.Latitude = deviceMessage.Latitude;
            this.Longitude = deviceMessage.Longitude;
            this.MessageId = deviceMessage.MessageId;
            this.DeviceId = deviceMessage.DeviceId;
            this.Timestamp = deviceMessage.Timestamp;
            this.Velocity = velocity;
        }

        public double Velocity { get; set; }
    }

    public class VelocityBatch
    {
        public VelocityMessage[] Messages;
    }
}
