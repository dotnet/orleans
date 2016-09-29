using System;
namespace GPSTracker.Common
{
    public class DeviceMessage
    {

        public DeviceMessage()
        { }

        public DeviceMessage(double latitude, double longitude, int messageId, int deviceId, DateTime timestamp)
        {
            this.Latitude = latitude;
            this.Longitude = longitude;
            this.MessageId = messageId;
            this.DeviceId = deviceId;
            this.Timestamp = timestamp;
        }

        public int DeviceId { get; set; }
        public int MessageId { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class MessageBatch
    {
        public DeviceMessage[] Messages;
    }
}
