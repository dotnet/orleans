using System.Diagnostics;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Tests.GoogleFirestore;
using Xunit.Abstractions;


namespace Orleans.Clustering.GoogleFirestore.Tests;

[TestCategory("Stress"), TestCategory("GoogleFirestore"), TestCategory("GoogleCloud")]
public class FirestoreDataManagerStressTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output = default!;
    private readonly List<FirestoreDataManager> _managers = [];
    private FirestoreOptions _options = default!;

    public FirestoreDataManagerStressTests(ITestOutputHelper output)
    {
        this._output = output;
    }

    [SkippableFact]
    public Task WriteMany_SinglePartition()
    {
        const string testName = "WriteMany_SinglePartition";
        const int iterations = 2000;
        const int batchSize = 1000;
        const int numPartitions = 1;

        return WriteMany(testName, numPartitions, iterations, batchSize);
    }

    [SkippableFact]
    public Task WriteMany_MultiPartition()
    {
        const string testName = "WriteMany_MultiPartition";
        const int iterations = 2000;
        const int batchSize = 1000;
        const int numPartitions = 100;

        return WriteMany(testName, numPartitions, iterations, batchSize);
    }

    private async Task WriteMany(string testName, int numPartitions, int iterations, int batchSize)
    {
        _output.WriteLine("Iterations={0}, Batch={1}, Partitions={2}", iterations, batchSize, numPartitions);
        var managers = Enumerable.Range(0, numPartitions)
            .Select(partition => new FirestoreDataManager(
                testName,
                $"Partition-{partition}",
                _options,
                NullLoggerFactory.Instance.CreateLogger<FirestoreDataManagerStressTests>()))
            .ToArray();
        _managers.AddRange(managers);
        await Task.WhenAll(managers.Select(manager => manager.Initialize()));

        var promises = new List<Task>();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            var dataObject = new DummyLoadEntity
            {
                Id = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringData = "This is a test string",
                BinaryData = new byte[128]
            };
            var promise = managers[i % managers.Length].UpsertEntity(dataObject);
            promises.Add(promise);
            if (promises.Count == batchSize)
            {
                await Task.WhenAll(promises);
                promises.Clear();
                _output.WriteLine("{0} has written {1} rows in {2} at {3} RPS",
                    testName, i + 1, sw.Elapsed, (i + 1) / sw.Elapsed.TotalSeconds);
            }
        }

        await Task.WhenAll(promises);
        sw.Stop();

        var counts = await Task.WhenAll(managers.Select(async manager =>
            (await manager.ReadAllEntities<DummyLoadEntity>()).Length));
        Assert.Equal(iterations, counts.Sum());

        _output.WriteLine("{0} completed. Wrote {1} entries to {2} partition(s) in {3} at {4} RPS",
            testName, iterations, numPartitions, sw.Elapsed, iterations / sw.Elapsed.TotalSeconds);
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_managers.Select(manager => manager.ClearCollection()));
    }

    public Task InitializeAsync()
    {
        _options = new FirestoreOptions
        {
            ProjectId = GoogleEmulatorHost.ProjectId,
            EmulatorHost = GoogleEmulatorHost.FirestoreEndpoint,
            RootCollectionName = $"orleans-test-{Guid.NewGuid():N}",
        };
        return Task.CompletedTask;
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
