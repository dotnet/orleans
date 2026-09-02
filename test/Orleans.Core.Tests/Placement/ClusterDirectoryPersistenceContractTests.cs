using System.Text.Json;
using System.Text.Json.Serialization;
using Newtonsoft.Json;
using NSubstitute;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.Placement;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestArea("Placement")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[Trait("Phase", "3")]
[Trait("FullyQualifiedName", "UnitTests.Placement.ClusterDirectoryPersistenceContractTests")]
public sealed class ClusterDirectoryPersistenceContractTests(TestEnvironmentFixture environment)
{
    private static readonly GrainId GrainId = GrainId.Create("persistence.test", "grain-1");
    private static readonly DateTimeOffset LeaseExpiration = new(2037, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [Fact]
    public void ClusterDirectoryEntry_BinaryPersistenceRoundTrip_PreservesVersionEpochFenceAndLease()
    {
        var expected = Entry("east", version: 17, epoch: 23, fence: 31);

        var payload = environment.Serializer.SerializeToArray(expected);
        var actual = environment.Serializer.Deserialize<ClusterDirectoryEntry>(payload);

        Assert.NotNull(actual);
        AssertEntry(expected, actual);
        Assert.NotEmpty(payload);
    }

    [Fact]
    public void ClusterDirectoryEntry_NewtonsoftPersistenceRoundTrip_PreservesVersionEpochFenceAndLease()
    {
        var expected = Entry("west", version: 19, epoch: 29, fence: 37);
        var settings = new JsonSerializerSettings();
        settings.Converters.Add(new NewtonsoftGrainIdConverter());

        var payload = JsonConvert.SerializeObject(expected, settings);
        var actual = JsonConvert.DeserializeObject<ClusterDirectoryEntry>(payload, settings);

        Assert.NotNull(actual);
        AssertEntry(expected, actual);
        Assert.Contains("\"FencingToken\":37", payload, StringComparison.Ordinal);
        Assert.Contains("\"TopologyEpoch\":29", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void ClusterDirectoryEntry_SystemTextJsonPersistenceRoundTrip_PreservesVersionEpochFenceAndLease()
    {
        var expected = Entry("north", version: 41, epoch: 43, fence: 47);
        var options = JsonOptions();

        var payload = System.Text.Json.JsonSerializer.Serialize(expected, options);
        var actual = System.Text.Json.JsonSerializer.Deserialize<ClusterDirectoryEntry>(payload, options);

        Assert.NotNull(actual);
        AssertEntry(expected, actual);
        Assert.Contains("\"FencingToken\":47", payload, StringComparison.Ordinal);
        Assert.Contains("\"LeaseExpiration\":\"2037-03-04T05:06:07+00:00\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalPersistence_ConditionalWrite_AcceptsCurrentFence()
    {
        var ownership = Entry("east", 5, 7, 11);
        var store = new FencedPersistence();
        store.Install(ownership, "initial");

        var accepted = store.TryWrite(ownership, "current-owner-write");

        Assert.True(accepted);
        Assert.Equal("current-owner-write", store.Read(GrainId));
        Assert.Equal(ownership, store.CurrentOwnership);
    }

    [Fact]
    public void ExternalPersistence_ConditionalWrite_RejectsPriorOwnerVersionEpochAndFence()
    {
        var prior = Entry("east", 5, 7, 11);
        var current = Entry("west", 6, 8, 12);
        var store = new FencedPersistence();
        store.Install(current, "current-value");

        var priorOwner = store.TryWrite(prior, "prior-owner");
        var priorVersion = store.TryWrite(Clone(current, version: current.Version - 1), "prior-version");
        var priorEpoch = store.TryWrite(Clone(current, epoch: current.TopologyEpoch - 1), "prior-epoch");
        var priorFence = store.TryWrite(Clone(current, fence: current.FencingToken - 1), "prior-fence");

        Assert.False(priorOwner);
        Assert.False(priorVersion);
        Assert.False(priorEpoch);
        Assert.False(priorFence);
        Assert.Equal("current-value", store.Read(GrainId));
        Assert.Equal(current, store.CurrentOwnership);
    }

    [Fact]
    public void ExternalPersistence_ReloadAfterRelocation_PreservesNewOwnerAndMonotonicFence()
    {
        var original = Entry("east", 9, 10, 9);
        var relocated = Entry("west", 10, 11, 10);
        var store = new FencedPersistence();
        store.Install(original, "before-move");
        store.Install(relocated, "after-move");

        var snapshot = store.Export();
        var reloaded = FencedPersistence.Reload(snapshot);

        Assert.Equal("after-move", reloaded.Read(GrainId));
        Assert.Equal(relocated, reloaded.CurrentOwnership);
        Assert.True(reloaded.CurrentOwnership!.FencingToken > original.FencingToken);
        Assert.False(reloaded.TryWrite(original, "stale-after-reload"));
    }

    [Fact]
    public void ExternalPersistence_ReloadAfterExpiryAndReacquire_RejectsStaleWriter()
    {
        var expired = Entry("east", 13, 15, 13);
        var reacquired = Entry("north", 14, 16, 14);
        var store = new FencedPersistence();
        store.Install(expired, "expired-owner");
        store.Install(reacquired, "reacquired-owner");

        var reloaded = FencedPersistence.Reload(store.Export());
        var staleAccepted = reloaded.TryWrite(expired, "stale-write");

        Assert.False(staleAccepted);
        Assert.Equal("reacquired-owner", reloaded.Read(GrainId));
        Assert.Equal("north", reloaded.CurrentOwnership!.ClusterId);
        Assert.Equal(14, reloaded.CurrentOwnership.FencingToken);
    }

    [Fact]
    public void ClusterOwnershipAccessor_Current_ExposesInvocationFenceAndClearsWithContext()
    {
        var expected = Entry("east", 21, 22, 23);
        var context = Substitute.For<IGrainContext>();
        context.GetComponent(typeof(ClusterDirectoryEntry)).Returns(expected);
        var accessor = new ClusterOwnershipAccessor();
        RuntimeContext.SetExecutionContext(context, out var originalContext);
        try
        {
            var current = accessor.Current;

            Assert.NotNull(current);
            Assert.Same(expected, current);
            Assert.Equal(23, current.FencingToken);
            Assert.Equal(22, current.TopologyEpoch);
        }
        finally
        {
            RuntimeContext.ResetExecutionContext(originalContext);
        }

        Assert.Null(accessor.Current);
    }

    private static ClusterDirectoryEntry Entry(string cluster, long version, long epoch, long fence)
        => new(GrainId, cluster, version, epoch, fence, LeaseExpiration);

    private static ClusterDirectoryEntry Clone(
        ClusterDirectoryEntry entry,
        long? version = null,
        long? epoch = null,
        long? fence = null)
        => new(
            entry.GrainId,
            entry.ClusterId,
            version ?? entry.Version,
            epoch ?? entry.TopologyEpoch,
            fence ?? entry.FencingToken,
            entry.LeaseExpiration);

    private static void AssertEntry(ClusterDirectoryEntry expected, ClusterDirectoryEntry actual)
    {
        Assert.Equal(expected.GrainId, actual.GrainId);
        Assert.Equal(expected.ClusterId, actual.ClusterId);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.TopologyEpoch, actual.TopologyEpoch);
        Assert.Equal(expected.FencingToken, actual.FencingToken);
        Assert.Equal(expected.LeaseExpiration, actual.LeaseExpiration);
    }

    private static JsonSerializerOptions JsonOptions()
    {
        var result = new JsonSerializerOptions();
        result.Converters.Add(new SystemTextGrainIdConverter());
        return result;
    }

    private sealed class FencedPersistence
    {
        private ClusterDirectoryEntry? _ownership;
        private string? _value;

        public ClusterDirectoryEntry? CurrentOwnership => _ownership;

        public void Install(ClusterDirectoryEntry ownership, string value)
        {
            if (_ownership is not null && ownership.FencingToken <= _ownership.FencingToken)
            {
                throw new InvalidOperationException("Replacement ownership must advance the fence.");
            }

            _ownership = ownership;
            _value = value;
        }

        public bool TryWrite(ClusterDirectoryEntry ownership, string value)
        {
            if (_ownership is null
                || ownership.GrainId != _ownership.GrainId
                || !string.Equals(ownership.ClusterId, _ownership.ClusterId, StringComparison.Ordinal)
                || ownership.Version != _ownership.Version
                || ownership.TopologyEpoch != _ownership.TopologyEpoch
                || ownership.FencingToken != _ownership.FencingToken)
            {
                return false;
            }

            _value = value;
            return true;
        }

        public string? Read(GrainId grainId) => grainId == _ownership?.GrainId ? _value : null;

        public byte[] Export()
            => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new PersistenceSnapshot(_ownership!, _value!),
                JsonOptions());

        public static FencedPersistence Reload(byte[] payload)
        {
            var snapshot = System.Text.Json.JsonSerializer.Deserialize<PersistenceSnapshot>(payload, JsonOptions())!;
            var result = new FencedPersistence();
            result.Install(snapshot.Ownership, snapshot.Value);
            return result;
        }
    }

    private sealed record PersistenceSnapshot(ClusterDirectoryEntry Ownership, string Value);

    private sealed class SystemTextGrainIdConverter : System.Text.Json.Serialization.JsonConverter<GrainId>
    {
        public override GrainId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => GrainId.Parse(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, GrainId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }

    private sealed class NewtonsoftGrainIdConverter : Newtonsoft.Json.JsonConverter<GrainId>
    {
        public override GrainId ReadJson(
            JsonReader reader,
            Type objectType,
            GrainId existingValue,
            bool hasExistingValue,
            Newtonsoft.Json.JsonSerializer serializer)
            => GrainId.Parse((string)reader.Value!);

        public override void WriteJson(
            JsonWriter writer,
            GrainId value,
            Newtonsoft.Json.JsonSerializer serializer)
            => writer.WriteValue(value.ToString());
    }
}
