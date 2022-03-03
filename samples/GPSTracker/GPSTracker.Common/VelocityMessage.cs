using Orleans.Concurrency;

namespace GPSTracker.Common;

[Immutable, Serializable]
public record class VelocityMessage(
    DeviceMessage DeviceMessage,
    double Velocity) :
    DeviceMessage(
        DeviceMessage.Latitude,
        DeviceMessage.Longitude,
        DeviceMessage.MessageId,
        DeviceMessage.DeviceId,
        DeviceMessage.Timestamp);

[Immutable, Serializable]
public record class VelocityBatch(VelocityMessage[] Messages);
