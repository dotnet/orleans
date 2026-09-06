using System.Reflection;
using Orleans;
using Orleans.GrainReferences;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.TestKit.Consistency;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[CollectionDefinition(Name)]
public sealed class TransactionValueSemanticsCollection : ICollectionFixture<TestEnvironmentFixture>
{
    public const string Name = nameof(TransactionValueSemanticsCollection);
}

[Collection(TransactionValueSemanticsCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class TransactionValueSemanticsTests
{
    private readonly TestEnvironmentFixture _fixture;

    public TransactionValueSemanticsTests(TestEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void AccessCounterSatisfiesEqualityAndAdditionContracts()
    {
        AssertEqual(default(AccessCounter), default);

        var original = new AccessCounter { Reads = 2, Writes = 3 };
        var equal = new AccessCounter { Reads = 2, Writes = 3 };
        AssertEqual(original, equal);
        AssertNotEqual(original, new AccessCounter { Reads = 1, Writes = 3 });
        AssertNotEqual(original, new AccessCounter { Reads = 2, Writes = 4 });
        AssertHashCollections(original, equal, new AccessCounter { Reads = 2, Writes = 4 });
        AssertEqual(
            new AccessCounter { Reads = 7, Writes = 11 },
            original + new AccessCounter { Reads = 5, Writes = 8 });
    }

    [Fact]
    public void AccessCounterSerializationContractIsPreserved()
    {
        var original = new AccessCounter { Reads = 2, Writes = 3 };

        var result = RoundTrip(original);

        AssertEqual(original, result);
        Assert.Equal(0U, GetFieldId<AccessCounter>(nameof(AccessCounter.Reads)));
        Assert.Equal(1U, GetFieldId<AccessCounter>(nameof(AccessCounter.Writes)));
    }

    [Fact]
    public void ParticipantIdDefaultEqualityIncludesRoles()
    {
        AssertEqual(default(ParticipantId), default);

        var reference = CreateGrainReference("resource");
        var name = new string("state".ToCharArray());
        var equalName = new string("state".ToCharArray());
        var original = new ParticipantId(name, reference, ParticipantId.Role.Resource);
        var equal = new ParticipantId(equalName, reference, ParticipantId.Role.Resource);
        var differentRole = new ParticipantId(name, reference, ParticipantId.Role.Manager);

        Assert.NotSame(name, equalName);
        Assert.Same(original.Reference, equal.Reference);
        AssertEqual(original, equal);
        AssertNotEqual(original, differentRole);
        AssertHashCollections(original, equal, differentRole);
    }

    [Fact]
    public void ParticipantIdUsesGrainReferenceEquality()
    {
        var firstReference = CreateGrainReference("resource");
        var equalReference = CreateGrainReference("resource");
        var differentReference = CreateGrainReference("other");
        var original = new ParticipantId("state", firstReference, ParticipantId.Role.Resource);
        var equal = new ParticipantId("state", equalReference, ParticipantId.Role.Resource);

        Assert.NotSame(firstReference, equalReference);
        Assert.Equal(firstReference, equalReference);
        AssertEqual(original, equal);
        AssertNotEqual(original, new ParticipantId("state", differentReference, ParticipantId.Role.Resource));
    }

    [Fact]
    public void ParticipantIdComparerIgnoresRoles()
    {
        var reference = CreateGrainReference("resource");
        var resource = new ParticipantId("state", reference, ParticipantId.Role.Resource);
        var manager = new ParticipantId("state", reference, ParticipantId.Role.Manager);
        var differentName = new ParticipantId("other", reference, ParticipantId.Role.Manager);

        AssertNotEqual(resource, manager);
        Assert.True(ParticipantId.Comparer.Equals(resource, manager));
        Assert.Equal(ParticipantId.Comparer.GetHashCode(resource), ParticipantId.Comparer.GetHashCode(manager));

        var set = new HashSet<ParticipantId>(ParticipantId.Comparer) { resource };
        var dictionary = new Dictionary<ParticipantId, string>(ParticipantId.Comparer) { [resource] = "stored" };
        Assert.Contains(manager, set);
        Assert.DoesNotContain(differentName, set);
        Assert.Equal("stored", dictionary[manager]);
        Assert.False(dictionary.ContainsKey(differentName));
    }

    [Fact]
    public void ParticipantIdSerializationContractIsPreserved()
    {
        var original = new ParticipantId(
            "state",
            CreateGrainReference("serialized-resource"),
            ParticipantId.Role.Resource | ParticipantId.Role.Manager);

        var result = RoundTrip(original);

        Assert.NotNull(result.Reference);
        AssertEqual(original, result);
        Assert.Equal(0U, GetPropertyId<ParticipantId>(nameof(ParticipantId.Name)));
        Assert.Equal(1U, GetPropertyId<ParticipantId>(nameof(ParticipantId.Reference)));
        Assert.Equal(2U, GetPropertyId<ParticipantId>(nameof(ParticipantId.SupportedRoles)));
    }

    [Fact]
    public void ObservationUsesAllFieldsAndOrdinaryStringEquality()
    {
        AssertEqual(default(Observation), default);

        var writer = new string("writer".ToCharArray());
        var equalWriter = new string("writer".ToCharArray());
        var executing = new string("executing".ToCharArray());
        var equalExecuting = new string("executing".ToCharArray());
        var original = CreateObservation(writer, executing);
        var equal = CreateObservation(equalWriter, equalExecuting);

        Assert.NotSame(writer, equalWriter);
        Assert.NotSame(executing, equalExecuting);
        AssertEqual(original, equal);
        AssertNotEqual(original, CreateObservation(writer, executing, grain: 2));
        AssertNotEqual(original, CreateObservation(writer, executing, seqNo: 4));
        AssertNotEqual(original, CreateObservation("other", executing));
        AssertNotEqual(original, CreateObservation(writer, "other"));
        AssertHashCollections(original, equal, CreateObservation(writer, executing, seqNo: 4));
    }

    [Fact]
    public void ObservationSerializationContractIsPreserved()
    {
        var original = CreateObservation("writer", "executing");

        var result = RoundTrip(original);

        AssertEqual(original, result);
        Assert.Equal(0U, GetPropertyId<Observation>(nameof(Observation.Grain)));
        Assert.Equal(1U, GetPropertyId<Observation>(nameof(Observation.SeqNo)));
        Assert.Equal(2U, GetPropertyId<Observation>(nameof(Observation.WriterTx)));
        Assert.Equal(3U, GetPropertyId<Observation>(nameof(Observation.ExecutingTx)));
    }

    private T RoundTrip<T>(T value) => _fixture.Serializer.Deserialize<T>(_fixture.Serializer.SerializeToArray(value))!;

    private static Observation CreateObservation(
        string writerTx,
        string executingTx,
        int grain = 1,
        int seqNo = 3) =>
        new()
        {
            Grain = grain,
            SeqNo = seqNo,
            WriterTx = writerTx,
            ExecutingTx = executingTx,
        };

    private static GrainReference CreateGrainReference(string key)
        => new TestGrainReference(GrainId.Create("transaction-value-semantics", key));

    private static uint GetFieldId<T>(string fieldName)
    {
        var field = typeof(T).GetField(fieldName);
        Assert.NotNull(field);
        var attribute = field.GetCustomAttribute<IdAttribute>();
        Assert.NotNull(attribute);
        return attribute.Id;
    }

    private static uint GetPropertyId<T>(string propertyName)
    {
        var property = typeof(T).GetProperty(propertyName);
        Assert.NotNull(property);
        var attribute = property.GetCustomAttribute<IdAttribute>();
        Assert.NotNull(attribute);
        return attribute.Id;
    }

    private static void AssertEqual(AccessCounter left, AccessCounter right)
    {
        AssertEqualContract(left, right);
        Assert.True(left == right);
        Assert.False(left != right);
    }

    private static void AssertNotEqual(AccessCounter left, AccessCounter right)
    {
        AssertNotEqualContract(left, right);
        Assert.False(left == right);
        Assert.True(left != right);
    }

    private static void AssertEqual(ParticipantId left, ParticipantId right)
    {
        AssertEqualContract(left, right);
        Assert.True(left == right);
        Assert.False(left != right);
    }

    private static void AssertNotEqual(ParticipantId left, ParticipantId right)
    {
        AssertNotEqualContract(left, right);
        Assert.False(left == right);
        Assert.True(left != right);
    }

    private static void AssertEqual(Observation left, Observation right)
    {
        AssertEqualContract(left, right);
        Assert.True(left == right);
        Assert.False(left != right);
    }

    private static void AssertNotEqual(Observation left, Observation right)
    {
        AssertNotEqualContract(left, right);
        Assert.False(left == right);
        Assert.True(left != right);
    }

    private static void AssertEqualContract<T>(T left, T right)
        where T : struct, IEquatable<T>
    {
        Assert.Equal(left, right);
        Assert.True(left.Equals(right));
        Assert.True(((object)left).Equals(right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    private static void AssertNotEqualContract<T>(T left, T right)
        where T : struct, IEquatable<T>
    {
        Assert.NotEqual(left, right);
        Assert.False(left.Equals(right));
        Assert.False(((object)left).Equals(right));
    }

    private static void AssertHashCollections<T>(T original, T equal, T unequal)
        where T : notnull
    {
        var set = new HashSet<T> { original };
        var dictionary = new Dictionary<T, string> { [original] = "stored" };

        Assert.Contains(equal, set);
        Assert.DoesNotContain(unequal, set);
        Assert.Equal("stored", dictionary[equal]);
        Assert.False(dictionary.ContainsKey(unequal));
    }

    private sealed class TestGrainReference(GrainId grainId)
        : GrainReference(
            new GrainReferenceShared(
                grainId.Type,
                default,
                interfaceVersion: 0,
                runtime: null!,
                invokeMethodOptions: default,
                codecProvider: null!,
                copyContextPool: null!,
                serviceProvider: null!),
            grainId.Key);

}
