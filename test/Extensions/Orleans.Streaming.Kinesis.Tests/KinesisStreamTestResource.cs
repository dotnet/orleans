using Amazon.Kinesis;
using Amazon.Kinesis.Model;

namespace Orleans.Streaming.Kinesis.Tests;

internal static class KinesisStreamTestResource
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(1);

    public static async Task Create(string streamName, CancellationToken cancellationToken)
    {
        await Delete(streamName, cancellationToken);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(OperationTimeout);
        using var client = CreateClient(streamName);
        var creationAttempted = false;
        try
        {
            creationAttempted = true;
            await client.CreateStreamAsync(
                new CreateStreamRequest { StreamName = streamName, ShardCount = 4 },
                cancellation.Token);

            while (true)
            {
                var response = await client.DescribeStreamAsync(
                    new DescribeStreamRequest { StreamName = streamName },
                    cancellation.Token);
                if (response.StreamDescription.StreamStatus == StreamStatus.ACTIVE)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token);
            }
        }
        catch
        {
            if (creationAttempted)
            {
                await DeleteForCleanup(streamName, cancellationToken);
            }

            throw;
        }
    }

    public static async Task Delete(string streamName, CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(OperationTimeout);
        using var client = CreateClient(streamName);
        try
        {
            await client.DeleteStreamAsync(
                new DeleteStreamRequest { StreamName = streamName },
                cancellation.Token);
        }
        catch (ResourceNotFoundException)
        {
            return;
        }

        while (true)
        {
            try
            {
                await client.DescribeStreamAsync(
                    new DescribeStreamRequest { StreamName = streamName },
                    cancellation.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token);
            }
            catch (ResourceNotFoundException)
            {
                return;
            }
        }
    }

    public static async Task DeleteForCleanup(string streamName, CancellationToken testCancellationToken)
    {
        using var cleanup = new CancellationTokenSource(OperationTimeout);
        try
        {
            await Delete(streamName, cleanup.Token);
        }
        catch (OperationCanceledException) when (testCancellationToken.IsCancellationRequested)
        {
            // Preserve the original test cancellation after bounded cleanup.
        }
    }

    private static IAmazonKinesis CreateClient(string streamName)
    {
        return KinesisAdapterFactory.CreateClient(new KinesisStreamOptions
        {
            ConnectionString = KinesisTestConstants.ConnectionString,
            StreamName = streamName,
        });
    }
}
