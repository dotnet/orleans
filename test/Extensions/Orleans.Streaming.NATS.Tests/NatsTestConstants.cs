using NATS.Client.Core;

namespace NATS.Tests;

public static class NatsTestConstants
{
    private static readonly Lazy<bool> _isNatsAvailable = new(() =>
    {
        try
        {
            var nats = new NatsConnection();
            nats.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)).Wait();
            return nats.ConnectionState == NatsConnectionState.Open;
        }
        catch
        {
            return false;
        }
    });

    public static bool IsNatsAvailable => _isNatsAvailable.Value;
}