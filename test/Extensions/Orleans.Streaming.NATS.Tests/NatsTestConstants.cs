using NATS.Client.Core;

namespace NATS.Tests;

public static class NatsTestConstants
{
    public static readonly NatsOpts NatsClientOptions = NatsOpts.Default with
    {
        Url = "nats://127.0.0.1:4222"
    };

    private static readonly Lazy<bool> _isNatsAvailable = new(
        () =>
        {
            try
            {
                return IsNatsAvailableAsync(Xunit.TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (Xunit.TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return false;
            }
        },
        LazyThreadSafetyMode.PublicationOnly);

    public static bool IsNatsAvailable => _isNatsAvailable.Value;

    public static NatsConnection CreateConnection() => new(NatsClientOptions);

    private static async Task<bool> IsNatsAvailableAsync(CancellationToken cancellationToken)
    {
        await using var nats = CreateConnection();

        await nats.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await nats.PingAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        return nats.ConnectionState == NatsConnectionState.Open && nats.ServerInfo?.JetStreamAvailable == true;
    }
}
