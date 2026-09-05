using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging;
using Orleans.AzureUtils;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.TestingHost.Utils;
using Xunit;

namespace Tester.AzureUtils
{
    /// <summary>
    /// Tests for Azure Queue Storage data manager operations including queue message handling and visibility timeouts.
    /// </summary>
    [TestCategory("AzureStorage"), TestCategory("Storage"), TestCategory("AzureQueue")]
    [TestSuite("Functional")]
    [TestProvider("AzureStorage")]
    [TestArea("Persistence")]
    public class AzureQueueDataManagerTests : IAsyncLifetime
    {
        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;
        public static string DeploymentId = "aqdatamanagertests";
        private string queueName = null!;

        public AzureQueueDataManagerTests()
        {
            TestUtils.CheckForAzureStorage();

            var loggerFactory = TestingUtils.CreateDefaultLoggerFactory(TestingUtils.CreateTraceFileName("Client", DateTime.Now.ToString("yyyyMMdd_hhmmss")));
            logger = loggerFactory.CreateLogger<AzureQueueDataManagerTests>();
            this.loggerFactory = loggerFactory;
        }

        public ValueTask InitializeAsync() => ValueTask.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            AzureQueueDataManager manager = await GetTableManager(queueName);
            await manager.DeleteQueue();
        }

        private async Task<AzureQueueDataManager> GetTableManager(string qName, TimeSpan? visibilityTimeout = null)
        {
            AzureQueueDataManager manager = new AzureQueueDataManager(this.loggerFactory, $"{qName}-{DeploymentId}", new AzureQueueOptions { MessageVisibilityTimeout = visibilityTimeout }.ConfigureTestDefaults());
            await manager.InitQueueAsync();
            return manager;
        }

        [Fact, TestCategory("Functional")]
        public async Task AQ_Standalone_1()
        {
            queueName = "test-1-" + Guid.NewGuid();
            AzureQueueDataManager manager = await GetTableManager(queueName);
            Assert.Equal(0, await manager.GetApproximateMessageCount());

            var inMessage = "Hello, World";
            await manager.AddQueueMessage(inMessage);
            //Nullable<int> count = manager.ApproximateMessageCount;
            Assert.Equal(1, await manager.GetApproximateMessageCount());

            var outMessage1 = await manager.PeekQueueMessage();
            Assert.NotNull(outMessage1);
            logger.LogInformation("PeekQueueMessage 1: {Message}", PrintQueueMessage(outMessage1));
            Assert.Equal(inMessage, outMessage1.MessageText);

            var outMessage2 = await manager.PeekQueueMessage();
            Assert.NotNull(outMessage2);
            logger.LogInformation("PeekQueueMessage 2: {Message}", PrintQueueMessage(outMessage2));
            Assert.Equal(inMessage, outMessage2.MessageText);

            var outMessage3 = await manager.GetQueueMessage();
            Assert.NotNull(outMessage3);
            logger.LogInformation("GetQueueMessage 3: {Message}", PrintQueueMessage(outMessage3));
            Assert.Equal(inMessage, outMessage3.MessageText);
            Assert.Equal(1, await manager.GetApproximateMessageCount());

            var outMessage4 = await manager.GetQueueMessage();
            Assert.Null(outMessage4);

            Assert.Equal(1, await manager.GetApproximateMessageCount());

            await manager.DeleteQueueMessage(outMessage3);
            Assert.Equal(0, await manager.GetApproximateMessageCount());
        }

        [Fact, TestCategory("Functional")]
        public async Task AQ_Standalone_2()
        {
            queueName = "test-2-" + Guid.NewGuid();
            AzureQueueDataManager manager = await GetTableManager(queueName);

            IEnumerable<QueueMessage> msgs = await manager.GetQueueMessages();
            Assert.Empty(msgs);

            int numMsgs = 10;
            List<Task> promises = new List<Task>();
            for (int i = 0; i < numMsgs; i++)
            {
                promises.Add(manager.AddQueueMessage(i.ToString()));
            }
            await Task.WhenAll(promises);
            Assert.Equal(numMsgs, await manager.GetApproximateMessageCount());

            var receivedMessages = await manager.GetQueueMessages(numMsgs);
            Assert.NotNull(receivedMessages);
            msgs = new List<QueueMessage>(receivedMessages);
            Assert.Equal(numMsgs, msgs.Count());
            Assert.Equal(numMsgs, await manager.GetApproximateMessageCount());

            promises = new List<Task>();
            foreach (var msg in msgs)
            {
                promises.Add(manager.DeleteQueueMessage(msg));
            }
            await Task.WhenAll(promises).WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, await manager.GetApproximateMessageCount());
        }

        [Fact, TestCategory("Functional")]
        public async Task AQ_Standalone_3_Init_MultipleThreads()
        {
            queueName = "test-4-" + Guid.NewGuid();

            const int NumThreads = 100;
            Task<bool>[] promises = new Task<bool>[NumThreads];

            for (int i = 0; i < NumThreads; i++)
            {
                promises[i] = Task.Run(
                    async () =>
                    {
                        AzureQueueDataManager manager = await GetTableManager(queueName);
                        return true;
                    },
                    TestContext.Current.CancellationToken);
            }
            await Task.WhenAll(promises).WaitAsync(TestContext.Current.CancellationToken);
        }

        [Fact, TestCategory("Functional")]
        public async Task AQ_Standalone_4()
        {
            var visibilityTimeout = TimeSpan.FromSeconds(2);

            queueName = "test-5-" + Guid.NewGuid();
            var manager = await GetTableManager(queueName, visibilityTimeout);

            var inMessage = "Hello, World";
            await manager.AddQueueMessage(inMessage);

            var outMessage = await manager.GetQueueMessage();
            Assert.NotNull(outMessage);
            logger.LogInformation("GetQueueMessage: {Message}", PrintQueueMessage(outMessage));
            Assert.Equal(inMessage, outMessage.MessageText);
            Assert.Equal(1, outMessage.DequeueCount);
            Assert.NotNull(outMessage.NextVisibleOn);

            // Azure owns the visibility transition, so observe it instead of racing the exact timeout boundary.
            await TestingUtils.WaitUntilAsync(
                async (lastTry, _) =>
                {
                    if (await manager.PeekQueueMessage() is not null)
                    {
                        return true;
                    }

                    if (lastTry)
                    {
                        Assert.Fail(
                            $"Queue message {outMessage.MessageId} did not become visible. "
                            + $"Azure reported {outMessage.NextVisibleOn:O} as the next visible time; "
                            + $"the current time is {DateTimeOffset.UtcNow:O}.");
                    }

                    return false;
                },
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMilliseconds(100),
                TestContext.Current.CancellationToken);

            var outMessage2 = await manager.GetQueueMessage();
            Assert.NotNull(outMessage2);
            Assert.Equal(outMessage.MessageId, outMessage2.MessageId);
            Assert.Equal(inMessage, outMessage2.MessageText);
            Assert.Equal(2, outMessage2.DequeueCount);

            await manager.DeleteQueueMessage(outMessage2);
        }

        private static string PrintQueueMessage(QueueMessage message)
        {
            return string.Format("QueueMessage: Id = {0}, NextVisibleTime = {1}, DequeueCount = {2}, PopReceipt = {3}, Content = {4}",
                    message.MessageId,
                    message.NextVisibleOn.HasValue ? LogFormatter.PrintDate(message.NextVisibleOn.Value.DateTime) : "",
                    message.DequeueCount,
                    message.PopReceipt,
                    message.MessageText);
        }

        private static string PrintQueueMessage(PeekedMessage message)
        {
            return string.Format("QueueMessage: Id = {0}, DequeueCount = {1}, Content = {2}",
                    message.MessageId,
                    message.DequeueCount,
                    message.MessageText);
        }
    }
}
