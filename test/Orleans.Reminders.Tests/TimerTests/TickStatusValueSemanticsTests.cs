using System.Reflection;
using Orleans;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.TimerTests;

[CollectionDefinition(Name)]
public sealed class TickStatusValueSemanticsCollection : ICollectionFixture<TestEnvironmentFixture>
{
    public const string Name = nameof(TickStatusValueSemanticsCollection);
}

[Collection(TickStatusValueSemanticsCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("BVT"), TestCategory("Reminders")]
public class TickStatusValueSemanticsTests
{
    private readonly TestEnvironmentFixture _fixture;

    public TickStatusValueSemanticsTests(TestEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void EqualAndDefaultValuesSatisfyEqualityContract()
    {
        AssertEqual(default, default);

        var first = CreateStatus();
        var second = CreateStatus();

        AssertEqual(first, second);
        Assert.False(first.Equals(new object()));
    }

    [Fact]
    public void EachFieldParticipatesInEquality()
    {
        var original = CreateStatus();

        AssertNotEqual(original, CreateStatus(firstTickTime: original.FirstTickTime.AddTicks(1)));
        AssertNotEqual(original, CreateStatus(period: original.Period.Add(TimeSpan.FromTicks(1))));
        AssertNotEqual(original, CreateStatus(currentTickTime: original.CurrentTickTime.AddTicks(1)));
    }

    [Fact]
    public void EqualValuesWorkAsHashKeys()
    {
        var original = CreateStatus();
        var equal = CreateStatus();
        var unequal = CreateStatus(period: TimeSpan.FromMinutes(2));
        var set = new HashSet<TickStatus> { original };
        var dictionary = new Dictionary<TickStatus, string> { [original] = "tick" };

        Assert.Contains(equal, set);
        Assert.DoesNotContain(unequal, set);
        Assert.Equal("tick", dictionary[equal]);
        Assert.False(dictionary.ContainsKey(unequal));
    }

    [Fact]
    public void SerializerRoundTripPreservesEquality()
    {
        var original = CreateStatus();

        var result = _fixture.Serializer.Deserialize<TickStatus>(_fixture.Serializer.SerializeToArray(original));

        AssertEqual(original, result);
    }

    [Fact]
    public void SerializationIdsArePreserved()
    {
        Assert.Equal(0U, GetId(nameof(TickStatus.FirstTickTime)));
        Assert.Equal(1U, GetId(nameof(TickStatus.Period)));
        Assert.Equal(2U, GetId(nameof(TickStatus.CurrentTickTime)));
    }

    private static TickStatus CreateStatus(
        DateTime? firstTickTime = null,
        TimeSpan? period = null,
        DateTime? currentTickTime = null) =>
        new(
            firstTickTime ?? new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            period ?? TimeSpan.FromMinutes(1),
            currentTickTime ?? new DateTime(2026, 1, 2, 3, 5, 5, DateTimeKind.Utc));

    private static uint GetId(string propertyName)
    {
        var property = typeof(TickStatus).GetProperty(propertyName);
        Assert.NotNull(property);
        var attribute = property.GetCustomAttribute<IdAttribute>();
        Assert.NotNull(attribute);
        return attribute.Id;
    }

    private static void AssertEqual(TickStatus left, TickStatus right)
    {
        Assert.Equal(left, right);
        Assert.True(left.Equals(right));
        Assert.True(((object)left).Equals(right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.True(left == right);
        Assert.False(left != right);
    }

    private static void AssertNotEqual(TickStatus left, TickStatus right)
    {
        Assert.NotEqual(left, right);
        Assert.False(left.Equals(right));
        Assert.False(((object)left).Equals(right));
        Assert.False(left == right);
        Assert.True(left != right);
    }
}
