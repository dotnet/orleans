using Amazon.Kinesis;
using Amazon.Kinesis.Model;

namespace Orleans.Streaming.Kinesis.Tests;

internal static class KinesisStreamTestResource
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(1);

    public static async Task Create(string streamName)
    {
        await Delete(streamName);

        using var cancellation = new CancellationTokenSource(OperationTimeout);
        using var client = CreateClient(streamName);
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

    public static async Task Delete(string streamName)
    {
        using var cancellation = new CancellationTokenSource(OperationTimeout);
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

    private static IAmazonKinesis CreateClient(string streamName)
    {
        return KinesisAdapterFactory.CreateClient(new KinesisStreamOptions
        {
            ConnectionString = KinesisTestConstants.ConnectionString,
            StreamName = streamName,
        });
    }
}
