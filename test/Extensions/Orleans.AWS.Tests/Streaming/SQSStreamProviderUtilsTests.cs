using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using OrleansAWSUtils.Streams;
using Xunit;

namespace AWSUtils.Tests.Streaming;

[TestSuite("BVT")]
[TestProvider("SQS")]
[TestArea("Streaming")]
[TestCategory("AWS"), TestCategory("SQS")]
public sealed class SQSStreamProviderUtilsTests
{
    [Theory]
    [InlineData(0, "cleanup-service")]
    [InlineData(-1, "cleanup-service")]
    [InlineData(int.MinValue, "cleanup-service")]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    public async Task DeleteAllUsedQueues_InvalidPartitionCount_ThrowsBeforeAccessingStorage(
        int partitionCount,
        string? serviceId)
    {
        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => SQSStreamProviderUtils.DeleteAllUsedQueues(
                "Topology",
                serviceId!,
                "invalid-connection-string",
                NullLoggerFactory.Instance,
                partitionCount,
                fifoQueue: false));

        Assert.Equal("partitionCount", exception.ParamName);
        Assert.Equal(partitionCount, exception.ActualValue);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(12, true)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    public async Task DeleteAllUsedQueues_DeletesExactlyTheConfiguredTopology(int? partitionCount, bool fifoQueue)
    {
        const string providerName = "Topology";
        const string serviceId = "cleanup-service";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(30));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var requests = new ConcurrentQueue<(string Operation, string QueueName)>();
        var server = ServeSqsRequestsAsync(listener, endpoint, requests, cancellation.Token);

        try
        {
            var connectionString = $"Service={endpoint}";
            var cleanup = partitionCount is { } count
                ? SQSStreamProviderUtils.DeleteAllUsedQueues(
                    providerName, serviceId, connectionString, NullLoggerFactory.Instance, count, fifoQueue)
                : fifoQueue
                    ? SQSStreamProviderUtils.DeleteAllUsedQueues(
                        providerName, serviceId, connectionString, NullLoggerFactory.Instance, fifoQueue: true)
                    : SQSStreamProviderUtils.DeleteAllUsedQueues(
                        providerName, serviceId, connectionString, NullLoggerFactory.Instance);
            await cleanup.WaitAsync(cancellation.Token);

            var expectedNames = Enumerable.Range(0, partitionCount ?? HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
                .Select(index => $"{serviceId}-topology-{index}{(fifoQueue ? ".fifo" : string.Empty)}")
                .Order()
                .ToArray();
            Assert.Equal(
                expectedNames,
                requests.Where(request => request.Operation == "GetQueueUrl").Select(request => request.QueueName).Order());
            Assert.Equal(
                expectedNames,
                requests.Where(request => request.Operation == "DeleteQueue").Select(request => request.QueueName).Order());
            Assert.Equal(expectedNames.Length * 2, requests.Count);
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await server;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            finally
            {
                listener.Stop();
            }
        }
    }

    private static async Task ServeSqsRequestsAsync(
        TcpListener listener,
        string endpoint,
        ConcurrentQueue<(string Operation, string QueueName)> requests,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
            Assert.NotNull(await reader.ReadLineAsync(cancellationToken));
            var contentLength = 0;
            string? operation = null;
            while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } header)
            {
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(header["Content-Length:".Length..], CultureInfo.InvariantCulture);
                }
                else if (header.StartsWith("X-Amz-Target:", StringComparison.OrdinalIgnoreCase))
                {
                    operation = header[(header.LastIndexOf('.') + 1)..];
                }
            }

            var body = new char[contentLength];
            Assert.Equal(contentLength, await reader.ReadBlockAsync(body.AsMemory(), cancellationToken));
            using var request = JsonDocument.Parse(new string(body));
            var queueName = operation switch
            {
                "GetQueueUrl" => request.RootElement.GetProperty("QueueName").GetString()!,
                "DeleteQueue" => new Uri(request.RootElement.GetProperty("QueueUrl").GetString()!).Segments[^1],
                _ => throw new InvalidOperationException($"Unexpected SQS operation '{operation}'."),
            };
            requests.Enqueue((operation, queueName));
            var responseBody = operation == "GetQueueUrl"
                ? JsonSerializer.Serialize(new { QueueUrl = $"{endpoint}/{queueName}" })
                : "{}";
            var response = Encoding.UTF8.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/x-amz-json-1.0\r\nContent-Length: {Encoding.UTF8.GetByteCount(responseBody)}\r\nConnection: close\r\n\r\n{responseBody}");
            await stream.WriteAsync(response, cancellationToken);
        }
    }
}
