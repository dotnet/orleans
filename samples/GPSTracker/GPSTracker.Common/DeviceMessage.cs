namespace GPSTracker.Common;

[Immutable, GenerateSerializer]
public record class DeviceMessage(
    double Latitude,
    double Longitude,
    long MessageId,
    int DeviceId,
    DateTime Timestamp);
