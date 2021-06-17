using System;
using Orleans.Concurrency;

namespace GPSTracker.Common
{
    [Immutable]
    [Serializable]
    public class DeviceMessage
    {
        public DeviceMessage()
        { }

        public DeviceMessage(double latitude, double longitude, long messageId, int deviceId, DateTime timestamp)
        {
            Latitude = latitude;
            Longitude = longitude;
            MessageId = messageId;
            DeviceId = deviceId;
            Timestamp = timestamp;
        }

        public int DeviceId { get; set; }
        public long MessageId { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
