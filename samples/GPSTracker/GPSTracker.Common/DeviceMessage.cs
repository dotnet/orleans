using Orleans.Concurrency;

namespace GPSTracker.Common;

[Immutable, Serializable]
public record class DeviceMessage(
    double Latitude,
    double Longitude,
    long MessageId,
    int DeviceId,
    DateTime Timestamp);