using System.Diagnostics;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Tests.GoogleFirestore;
using Xunit.Abstractions;


namespace Orleans.Tests.Google;

[TestCategory("Stress"), TestCategory("GoogleFirestore"), TestCategory("GoogleCloud")]
public class FirestoreDataManagerStressTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output = default!;
    private FirestoreDataManager _manager = default!;

    public FirestoreDataManagerStressTests(ITestOutputHelper output)
    {
        this._output = output;
    }

    [Fact]
    public void WriteAlot_SinglePartition()
    {
        const string testName = "WriteAlot_SinglePartition";
        const int iterations = 2000;
        const int batchSize = 1000;
        const int numPartitions = 1;

        // Write some data
        WriteAlot_Async(testName, numPartitions, iterations, batchSize);
    }

    [Fact]
    public void WriteAlot_MultiPartition()
    {
        const string testName = "WriteAlot_MultiPartition";
        const int iterations = 2000;
        const int batchSize = 1000;
        const int numPartitions = 100;

        // Write some data
        WriteAlot_Async(testName, numPartitions, iterations, batchSize);
    }

    private void WriteAlot_Async(string testName, int numPartitions, int iterations, int batchSize)
    {
        _output.WriteLine("Iterations={0}, Batch={1}, Partitions={2}", iterations, batchSize, numPartitions);
        var promises = new List<Task>();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            var dataObject = new DummyLoadEntity
            {
                StringData = "This is a test string",
                BinaryData = new byte[128]
            };
            Random.Shared.NextBytes(dataObject.BinaryData);
            var promise = _manager.UpsertEntity(dataObject);
            promises.Add(promise);
            if (i % batchSize == 0 && i > 0)
            {
                Task.WhenAll(promises);
                promises.Clear();
                _output.WriteLine("{0} has written {1} rows in {2} at {3} RPS",
                    testName, i, sw.Elapsed, i / sw.Elapsed.TotalSeconds);
            }
        }
        Task.WhenAll(promises);
        sw.Stop();
        _output.WriteLine("{0} completed. Wrote {1} entries to {2} partition(s) in {3} at {4} RPS",
            testName, iterations, numPartitions, sw.Elapsed, iterations / sw.Elapsed.TotalSeconds);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        await GoogleEmulatorHost.Instance.EnsureStarted();

        var options = new FirestoreOptions
        {
            ProjectId = $"orleans-test-{Guid.NewGuid():N}",
            EmulatorHost = GoogleEmulatorHost.FirestoreEndpoint
        };

        this._manager = new FirestoreDataManager(
            "Test",
            "Test",
            options,
            NullLoggerFactory.Instance.CreateLogger<FirestoreDataManagerStressTests>());

        await this._manager.Initialize();
    }

    [FirestoreData]
    private class DummyLoadEntity : FirestoreEntity
    {
        [FirestoreProperty("BinaryData")]
        public byte[] BinaryData { get; set; } = default!;

        [FirestoreProperty("StringData")]
        public string StringData { get; set; } = default!;

        public override IDictionary<string, object?> GetFields()
        {
            return new Dictionary<string, object?>
            {
                { "BinaryData", BinaryData },
                { "StringData", StringData },
            };
        }
    }
}
