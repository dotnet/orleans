using NATS.Client.Core;

namespace NATS.Tests;

public static class NatsTestConstants
{
    public static readonly NatsOpts NatsClientOptions = NatsOpts.Default with
    {
        Url = "nats://127.0.0.1:4222"
    };

    private static readonly Lazy<bool> _isNatsAvailable = new(() =>
    {
        try
        {
            return IsNatsAvailableAsync().GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
    });

    public static bool IsNatsAvailable => _isNatsAvailable.Value;

    public static NatsConnection CreateConnection() => new(NatsClientOptions);

    private static async Task<bool> IsNatsAvailableAsync()
    {
        await using var nats = CreateConnection();

        await nats.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await nats.PingAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        return nats.ConnectionState == NatsConnectionState.Open && nats.ServerInfo?.JetStreamAvailable == true;
    }
}
