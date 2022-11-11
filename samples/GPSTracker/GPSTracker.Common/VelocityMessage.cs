namespace GPSTracker.Common;

[Immutable, GenerateSerializer]
public record class VelocityMessage(
    DeviceMessage DeviceMessage,
    double Velocity) :
    DeviceMessage(
        DeviceMessage.Latitude,
        DeviceMessage.Longitude,
        DeviceMessage.MessageId,
        DeviceMessage.DeviceId,
        DeviceMessage.Timestamp);

[Immutable, GenerateSerializer]
public record class VelocityBatch(VelocityMessage[] Messages);
