using Google.Api.Gax;
using Google.Cloud.Firestore;

namespace Orleans.Clustering.Firestore.Tests;

[TestSuite("Functional")]
[TestProvider("GoogleCloud")]
[TestCategory("GoogleCloud"), TestCategory("Functional")]
public class FirestoreEmulatorTests
{
    [Fact]
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
}