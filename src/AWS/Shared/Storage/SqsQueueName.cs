namespace OrleansAWSUtils.Storage;

internal static class SqsQueueName
{
    public static string Create(string queueName, bool fifoQueue, string serviceId)
        => $"{(string.IsNullOrWhiteSpace(serviceId) ? string.Empty : $"{serviceId}-")}{queueName}{(fifoQueue ? ".fifo" : string.Empty)}";

    public static string Create(string providerName, int partition, bool fifoQueue, string serviceId)
        => Create($"{providerName.ToLowerInvariant()}-{partition}", fifoQueue, serviceId);
}
