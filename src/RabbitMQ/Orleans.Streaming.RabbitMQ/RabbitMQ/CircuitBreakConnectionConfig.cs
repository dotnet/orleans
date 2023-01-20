namespace Orleans.Streaming.RabbitMQ.RabbitMQ;

/// <summary>
/// Circuit break configuration to create the RabbitMQ stream connection
/// </summary>
public record CircuitBreakConnectionConfig
{
    /// <summary>
    /// The amount of retries the circuit will try to connect until it is open
    /// </summary>
    public int RetryTimesUntilBreak { get; set; } = 4;

    /// <summary>
    /// The waiting time the circuit will be open
    /// </summary>
    public TimeSpan WaitingTime { get; set; } = TimeSpan.FromSeconds(5);

    public void Deconstruct(out int retryTimesUntilBreak, out TimeSpan waitingTime)
    {
        retryTimesUntilBreak = RetryTimesUntilBreak;
        waitingTime = WaitingTime;
    }
}