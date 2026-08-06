using Orleans.Runtime;

internal sealed class ClientConnectRetryFilter : IClientConnectionRetryFilter
{
    private int _retryCount = 0;
    private const int MaxRetry = 5;
    private const int Delay = 1_500;

    public async Task<bool> ShouldRetryConnectionAttempt(
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (_retryCount >= MaxRetry)
        {
            return false;
        }

        if (!cancellationToken.IsCancellationRequested &&
            exception is SiloUnavailableException siloUnavailableException)
        {
            await Task.Delay(++ _retryCount * Delay, cancellationToken);
            return true;
        }

        return false;
    }
}
