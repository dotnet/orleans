using Orleans.Journaling.Ranges;
using Orleans.Runtime;

namespace Orleans.Journaling.Garnet.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class GarnetJournalStorageOptionsTests
{
    private static GarnetJournalStorageOptions CreateValid() => new() { MetaStore = new InMemoryRangeMetaStore(), HostId = "host" };

    [Fact]
    public void Validator_AcceptsValidOptions() => new GarnetJournalStorageOptionsValidator(CreateValid()).ValidateConfiguration();

    public static TheoryData<string> InvalidOptions => [.. InvalidOptionsByReason.Keys];

    private static readonly Dictionary<string, Action<GarnetJournalStorageOptions>> InvalidOptionsByReason = new()
    {
        ["no MetaStore"] = options => options.MetaStore = null,
        ["empty HostId"] = options => options.HostId = " ",
        ["bucket count"] = options => options.BucketCount = 3,
        ["invalid initial map"] = options => options.InitialRangeMap = new RangeMap("map", 4, [], [], RangeMap.CreateUniform(4, [new(new PartitionId("p0"), new PartitionHosting("h", 0, "p0"))]).Partitions),
        ["compaction threshold"] = options => options.CompactionThresholdBytes = -1,
        ["operation timeout"] = options => options.OperationTimeout = TimeSpan.Zero,
        ["log memory size"] = options => options.LogMemorySize = "",
        ["tombstone retention"] = options => options.TombstoneRetention = TimeSpan.Zero,
        ["migration interval"] = options => options.MigrationInterval = TimeSpan.Zero,
        ["marker collection interval"] = options => options.MarkerCollectionInterval = TimeSpan.Zero,
    };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Validator_RejectsInvalidOptions(string reason)
    {
        var options = CreateValid();
        InvalidOptionsByReason[reason](options);
        Assert.Throws<OrleansConfigurationException>(new GarnetJournalStorageOptionsValidator(options).ValidateConfiguration);
    }

    [Theory]
    [InlineData("p0", true)]
    [InlineData("nested/p0", true)]
    [InlineData("../p0", false)]
    [InlineData(".", false)]
    [InlineData("nested/../../p0", false)]
    public void StorageLocation_MustBeWithinTheDataDirectory(string location, bool valid)
    {
        var root = Path.Combine(Path.GetTempPath(), "garnet-root");
        var id = new PartitionId("p0");
        if (valid)
        {
            Assert.StartsWith(Path.GetFullPath(root), GarnetJournalPartitionManager.ResolveStorageLocation(root, id, location));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => GarnetJournalPartitionManager.ResolveStorageLocation(root, id, location));
        }

        Assert.Throws<InvalidOperationException>(() => GarnetJournalPartitionManager.ResolveStorageLocation(root, id, Path.GetFullPath(Path.Combine(root, "p0"))));
    }
}
