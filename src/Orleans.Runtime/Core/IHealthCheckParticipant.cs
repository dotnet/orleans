namespace Orleans.Runtime
{
    /// <summary>
    /// Interface for health check participants (which are being polled by Watchdog)
    /// </summary>
    public interface IHealthCheckParticipant : IHealthCheckable
    {
    }
}

