using Google.Cloud.Firestore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Orleans.Clustering.Firestore.Tests;

public class FirestoreDataManagerCancellationTests
{
    [Fact]
    public async Task ReadEntityHonorsCancellationToken()
    {
        var options = new FirestoreOptions
        {
            ProjectId = "orleans-test",
            EmulatorHost = "127.0.0.1:1",
        };
        var manager = new FirestoreDataManager(
            "Test",
            "Cancellation",
            options,
            NullLoggerFactory.Instance.CreateLogger<FirestoreDataManager>());
        var cancellationToken = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ReadEntity<TestEntity>(Guid.NewGuid().ToString(), cancellationToken));
    }

    [FirestoreData]
    private sealed class TestEntity : FirestoreEntity
    {
        public override IDictionary<string, object?> GetFields() => new Dictionary<string, object?>();
    }
}
