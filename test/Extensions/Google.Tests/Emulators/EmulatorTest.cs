using Google.Api.Gax;
using Google.Protobuf;
using Google.Cloud.Firestore;
using Google.Cloud.PubSub.V1;
using Google.Cloud.Storage.V1;

namespace Orleans.Tests.Google;

[TestCategory("GoogleCloud"), TestCategory("Functional")]
public class GoogleEmulatorTest
{
    [SkippableFact]
    public async Task EnsureFirestoreTest()
    {
        Assert.NotNull(GoogleEmulatorHost.FirestoreEndpoint);

        var id = $"orleans-test-{Guid.NewGuid():N}";

        var db = new FirestoreDbBuilder
        {
            ProjectId = id,
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.Build();

        var collection = db.Collection("users");
        var document = await collection.AddAsync(new { Name = new { First = "Ada", Last = "Lovelace" }, Born = 1815 });
        var snapshot = await document.GetSnapshotAsync();

        Assert.Equal("Ada", snapshot.GetValue<string>("Name.First"));
        Assert.Equal("Lovelace", snapshot.GetValue<string>("Name.Last"));
        Assert.Equal(1815, snapshot.GetValue<int>("Born"));
    }

    [SkippableFact]
    public async Task EnsurePubSubTest()
    {
        Assert.NotNull(GoogleEmulatorHost.PubSubEndpoint);

        var id = $"orleans-test-{Guid.NewGuid():N}";

        var topicId = "test-topic";
        var subscriptionId = "test-subscription";

        var publisher = new PublisherServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.Build();

        var topicName = new TopicName(id, topicId);
        publisher.CreateTopic(topicName);

        var subscriber = new SubscriberServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.Build();

        var subscriptionName = new SubscriptionName(id, subscriptionId);
        await subscriber.CreateSubscriptionAsync(subscriptionName, topicName, pushConfig: null, ackDeadlineSeconds: 60);

        var message = new PubsubMessage
        {
            Data = ByteString.CopyFromUtf8("Hello, Pubsub"),
            Attributes =
            {
                { "description", "Simple text message" }
            }
        };
        await publisher.PublishAsync(topicName, new[] { message });

        var response = await subscriber.PullAsync(subscriptionName, maxMessages: 10);
        foreach (var received in response.ReceivedMessages)
        {
            var msg = received.Message;
            Console.WriteLine($"Received message {msg.MessageId} published at {msg.PublishTime.ToDateTime()}");
            Console.WriteLine($"Text: '{msg.Data.ToStringUtf8()}'");
        }

        await subscriber.AcknowledgeAsync(subscriptionName, response.ReceivedMessages.Select(m => m.AckId));

        await subscriber.DeleteSubscriptionAsync(subscriptionName);
        await publisher.DeleteTopicAsync(topicName);
    }

    // [SkippableFact]
    // public async Task EnsureStorageTest()
    // {
    //     Assert.NotNull(GoogleEmulatorHost.StorageEndpoint);

    //     var client = new StorageClientBuilder()
    //     {
    //         BaseUri = GoogleEmulatorHost.StorageEndpoint,
    //         UnauthenticatedAccess = true
    //     }.Build();

    //     var id = $"orleans-test-{Guid.NewGuid():N}";
    //     var bucketName = Guid.NewGuid().ToString();
    //     await client.CreateBucketAsync(id, bucketName);

    //     var content = System.Text.Encoding.UTF8.GetBytes("hello, world");

    //     using var ms = new MemoryStream(content);
    //     var file1 = "file1.txt";
    //     var obj1 = await client.UploadObjectAsync(bucketName, file1, "text/plain", ms);
    //     var file2 = "folder1/file2.txt";
    //     var obj2 = await client.UploadObjectAsync(bucketName, file2, "text/plain", ms);

    //     var obj1Result = await client.GetObjectAsync(bucketName, file1);
    //     var obj2Result = await client.GetObjectAsync(bucketName, file2);

    //     Assert.Equal(file1, obj1Result.Name);
    //     Assert.Equal(file2, obj2Result.Name);
    // }
}