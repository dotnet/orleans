using System.Reflection;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Statistics;
using TestExtensions;
using UnitTests.Serialization;
using Xunit;

namespace UnitTests.General;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public class CoreAbstractionsValueSemanticsTests
{
    private readonly TestEnvironmentFixture _fixture;

    public CoreAbstractionsValueSemanticsTests(TestEnvironmentFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void DeactivationReason_EqualFieldsAndDefaultValues_SatisfyEqualityContract()
    {
        var defaultValue = default(DeactivationReason);
        var equalDefaultValue = default(DeactivationReason);
        var expected = new DeactivationReason(DeactivationReasonCode.ApplicationRequested, "requested");
        var actual = new DeactivationReason(
            DeactivationReasonCode.ApplicationRequested,
            new string("requested".ToCharArray()));

        Assert.Null(defaultValue.Description);
        Assert.Equal(DeactivationReasonCode.None, defaultValue.ReasonCode);
        Assert.Null(defaultValue.Exception);
        AssertEqual(defaultValue, equalDefaultValue);

        Assert.Equal("requested", actual.Description);
        Assert.Equal(DeactivationReasonCode.ApplicationRequested, actual.ReasonCode);
        Assert.Null(actual.Exception);
        AssertEqual(expected, actual);
    }

    [Fact]
    public void DeactivationReason_NullSameAndDistinctExceptionReferences_UseReferenceIdentity()
    {
        var withoutException = new DeactivationReason(DeactivationReasonCode.ApplicationError, null, "failure");
        var alsoWithoutException = new DeactivationReason(DeactivationReasonCode.ApplicationError, null, "failure");
        AssertEqual(withoutException, alsoWithoutException);

        var sharedException = new InvalidOperationException("failure");
        var withSharedException = new DeactivationReason(DeactivationReasonCode.ApplicationError, sharedException, "failure");
        var alsoWithSharedException = new DeactivationReason(DeactivationReasonCode.ApplicationError, sharedException, "failure");
        Assert.Same(withSharedException.Exception, alsoWithSharedException.Exception);
        AssertEqual(withSharedException, alsoWithSharedException);

        var firstException = new InvalidOperationException("failure");
        var secondException = new InvalidOperationException("failure");
        var withFirstException = new DeactivationReason(DeactivationReasonCode.ApplicationError, firstException, "failure");
        var withSecondException = new DeactivationReason(DeactivationReasonCode.ApplicationError, secondException, "failure");
        Assert.NotSame(withFirstException.Exception, withSecondException.Exception);
        AssertNotEqual(withFirstException, withSecondException);
    }

    [Fact]
    public void DeactivationReason_EachDifferentField_IsUnequal()
    {
        var exception = new InvalidOperationException("failure");
        var description = "failure";
        var equalDescription = new string(description.ToCharArray());
        var original = new DeactivationReason(DeactivationReasonCode.ApplicationError, exception, description);
        var equal = new DeactivationReason(DeactivationReasonCode.ApplicationError, exception, equalDescription);

        Assert.NotSame(description, equalDescription);
        AssertEqual(original, equal);

        AssertNotEqual(
            original,
            new DeactivationReason(DeactivationReasonCode.ActivationFailed, exception, description));
        AssertNotEqual(
            original,
            new DeactivationReason(DeactivationReasonCode.ApplicationError, exception, "different"));
        AssertNotEqual(
            original,
            new DeactivationReason(
                DeactivationReasonCode.ApplicationError,
                new InvalidOperationException("failure"),
                description));
    }

    [Fact]
    public void DeactivationReason_EqualAndUnequalValues_UseHashSetAndDictionary()
    {
        var exception = new InvalidOperationException("failure");
        var original = new DeactivationReason(DeactivationReasonCode.ApplicationError, exception, "failure");
        var equal = new DeactivationReason(DeactivationReasonCode.ApplicationError, exception, "failure");
        var unequal = new DeactivationReason(DeactivationReasonCode.ApplicationError, exception, "different");

        AssertHashCollections(original, equal, unequal);
    }

    [Fact]
    public void Immutable_DefaultNullAndScalarValues_SatisfyEqualityContract()
    {
        var defaultValue = default(Immutable<string>);
        var explicitNull = new Immutable<string>(null!);
        var expectedScalar = new Immutable<int>(42);
        var actualScalar = new Immutable<int>(42);

        Assert.Null(defaultValue.Value);
        Assert.Null(explicitNull.Value);
        AssertEqual(defaultValue, explicitNull);

        Assert.Equal(42, actualScalar.Value);
        AssertEqual(expectedScalar, actualScalar);
        Assert.False(actualScalar.Equals((object)42));
    }

    [Fact]
    public void Immutable_ArrayValue_UsesReferenceIdentityNotSequenceEquality()
    {
        int[] sharedArray = [1, 2, 3];
        int[] distinctArray = [1, 2, 3];
        var first = new Immutable<int[]>(sharedArray);
        var withSharedArray = new Immutable<int[]>(sharedArray);
        var withDistinctArray = new Immutable<int[]>(distinctArray);
        var nullArray = new Immutable<int[]>(null!);
        var alsoNullArray = new Immutable<int[]>(null!);

        Assert.Same(first.Value, withSharedArray.Value);
        AssertEqual(first, withSharedArray);

        Assert.NotSame(first.Value, withDistinctArray.Value);
        AssertNotEqual(first, withDistinctArray);

        Assert.Null(nullArray.Value);
        Assert.Null(alsoNullArray.Value);
        AssertEqual(nullArray, alsoNullArray);
    }

    [Fact]
    public void Immutable_EqualAndUnequalValues_UseHashSetAndDictionary()
    {
        AssertHashCollections(new Immutable<int>(42), new Immutable<int>(42), new Immutable<int>(43));

        int[] sharedArray = [1, 2, 3];
        int[] distinctArray = [1, 2, 3];
        Assert.Same(sharedArray, new Immutable<int[]>(sharedArray).Value);
        Assert.NotSame(sharedArray, distinctArray);
        AssertHashCollections(
            new Immutable<int[]>(sharedArray),
            new Immutable<int[]>(sharedArray),
            new Immutable<int[]>(distinctArray));
    }

    [Fact]
    public void Immutable_IntSerializerRoundTrip_PreservesEquality()
    {
        var original = new Immutable<int>(8675309);

        var result = _fixture.Serializer.RoundTripSerializationForTesting(original);

        Assert.Equal(8675309, result.Value);
        AssertEqual(original, result);
    }

    [Fact]
    public void Immutable_ValueSerializationId_IsZero()
    {
        var valueField = typeof(Immutable<int>).GetField(nameof(Immutable<int>.Value));

        Assert.NotNull(valueField);
        var id = valueField.GetCustomAttribute<IdAttribute>();
        Assert.NotNull(id);
        Assert.Equal(0U, id.Id);
    }

    [Fact]
    public void EnvironmentStatistics_EqualAndDefaultValues_SatisfyEqualityContract()
    {
        var defaultValue = default(EnvironmentStatistics);
        var equalDefaultValue = default(EnvironmentStatistics);
        var expected = CreateEnvironmentStatistics();
        var actual = CreateEnvironmentStatistics();

        Assert.Equal(0f, defaultValue.FilteredCpuUsagePercentage);
        Assert.Equal(0L, defaultValue.FilteredMemoryUsageBytes);
        Assert.Equal(0L, defaultValue.FilteredAvailableMemoryBytes);
        Assert.Equal(0L, defaultValue.MaximumAvailableMemoryBytes);
        Assert.Equal(0f, defaultValue.RawCpuUsagePercentage);
        Assert.Equal(0L, defaultValue.RawMemoryUsageBytes);
        Assert.Equal(0L, defaultValue.RawAvailableMemoryBytes);
        AssertEqual(defaultValue, equalDefaultValue);

        Assert.Equal(12.5f, actual.FilteredCpuUsagePercentage);
        Assert.Equal(200L, actual.FilteredMemoryUsageBytes);
        Assert.Equal(700L, actual.FilteredAvailableMemoryBytes);
        Assert.Equal(1_000L, actual.MaximumAvailableMemoryBytes);
        Assert.Equal(34.5f, actual.RawCpuUsagePercentage);
        Assert.Equal(300L, actual.RawMemoryUsageBytes);
        Assert.Equal(600L, actual.RawAvailableMemoryBytes);
        AssertEqual(expected, actual);
    }

    [Fact]
    public void EnvironmentStatistics_EachDifferentField_IsUnequal()
    {
        var original = CreateEnvironmentStatistics();

        AssertNotEqual(original, CreateEnvironmentStatistics(filteredCpuUsagePercentage: 13.5f));
        AssertNotEqual(original, CreateEnvironmentStatistics(filteredMemoryUsageBytes: 201L));
        AssertNotEqual(original, CreateEnvironmentStatistics(filteredAvailableMemoryBytes: 701L));
        AssertNotEqual(original, CreateEnvironmentStatistics(maximumAvailableMemoryBytes: 1_001L));
        AssertNotEqual(original, CreateEnvironmentStatistics(rawCpuUsagePercentage: 35.5f));
        AssertNotEqual(original, CreateEnvironmentStatistics(rawMemoryUsageBytes: 301L));
        AssertNotEqual(original, CreateEnvironmentStatistics(rawAvailableMemoryBytes: 601L));
    }

    [Fact]
    public void EnvironmentStatistics_EqualAndUnequalValues_UseHashSetAndDictionary()
    {
        var original = CreateEnvironmentStatistics();
        var equal = CreateEnvironmentStatistics();
        var unequal = CreateEnvironmentStatistics(rawMemoryUsageBytes: 301L);

        AssertHashCollections(original, equal, unequal);
    }

    [Fact]
    public void EnvironmentStatistics_SerializerRoundTrip_PreservesEquality()
    {
        var original = CreateEnvironmentStatistics();

        var result = _fixture.Serializer.RoundTripSerializationForTesting(original);

        Assert.Equal(12.5f, result.FilteredCpuUsagePercentage);
        Assert.Equal(200L, result.FilteredMemoryUsageBytes);
        Assert.Equal(700L, result.FilteredAvailableMemoryBytes);
        Assert.Equal(1_000L, result.MaximumAvailableMemoryBytes);
        Assert.Equal(34.5f, result.RawCpuUsagePercentage);
        Assert.Equal(300L, result.RawMemoryUsageBytes);
        Assert.Equal(600L, result.RawAvailableMemoryBytes);
        AssertEqual(original, result);
    }

    [Fact]
    public void EnvironmentStatistics_SerializationIdsAndAlias_ArePreserved()
    {
        var expectedIds = new Dictionary<string, uint>
        {
            [nameof(EnvironmentStatistics.FilteredCpuUsagePercentage)] = 0,
            [nameof(EnvironmentStatistics.FilteredMemoryUsageBytes)] = 1,
            [nameof(EnvironmentStatistics.FilteredAvailableMemoryBytes)] = 2,
            [nameof(EnvironmentStatistics.MaximumAvailableMemoryBytes)] = 3,
            [nameof(EnvironmentStatistics.RawCpuUsagePercentage)] = 4,
            [nameof(EnvironmentStatistics.RawMemoryUsageBytes)] = 5,
            [nameof(EnvironmentStatistics.RawAvailableMemoryBytes)] = 6,
        };

        foreach (var (memberName, expectedId) in expectedIds)
        {
            var field = typeof(EnvironmentStatistics).GetField(memberName);
            Assert.NotNull(field);
            var id = field.GetCustomAttribute<IdAttribute>();
            Assert.NotNull(id);
            Assert.Equal(expectedId, id.Id);
        }

        var alias = typeof(EnvironmentStatistics).GetCustomAttribute<AliasAttribute>();
        Assert.NotNull(alias);
        Assert.Equal("Orleans.Statistics.EnvironmentStatistics", alias.Alias);
    }

    [Fact]
    public void GrainTimerCreationOptions_EqualAndDefaultValues_SatisfyEqualityContract()
    {
        var defaultValue = default(GrainTimerCreationOptions);
        var equalDefaultValue = default(GrainTimerCreationOptions);
        var expected = CreateGrainTimerCreationOptions();
        var actual = CreateGrainTimerCreationOptions();

        Assert.Equal(TimeSpan.Zero, defaultValue.DueTime);
        Assert.Equal(TimeSpan.Zero, defaultValue.Period);
        Assert.False(defaultValue.Interleave);
        Assert.False(defaultValue.KeepAlive);
        AssertEqual(defaultValue, equalDefaultValue);

        Assert.Equal(TimeSpan.FromSeconds(2), actual.DueTime);
        Assert.Equal(TimeSpan.FromMinutes(3), actual.Period);
        Assert.True(actual.Interleave);
        Assert.True(actual.KeepAlive);
        AssertEqual(expected, actual);
    }

    [Fact]
    public void GrainTimerCreationOptions_EachDifferentOption_IsUnequal()
    {
        var original = CreateGrainTimerCreationOptions();

        AssertNotEqual(original, CreateGrainTimerCreationOptions(dueTime: TimeSpan.FromSeconds(3)));
        AssertNotEqual(original, CreateGrainTimerCreationOptions(period: TimeSpan.FromMinutes(4)));
        AssertNotEqual(original, CreateGrainTimerCreationOptions(interleave: false));
        AssertNotEqual(original, CreateGrainTimerCreationOptions(keepAlive: false));
    }

    [Fact]
    public void GrainTimerCreationOptions_EqualAndUnequalValues_UseHashSetAndDictionary()
    {
        var original = CreateGrainTimerCreationOptions();
        var equal = CreateGrainTimerCreationOptions();
        var unequal = CreateGrainTimerCreationOptions(keepAlive: false);

        AssertHashCollections(original, equal, unequal);
    }

    private static EnvironmentStatistics CreateEnvironmentStatistics(
        float filteredCpuUsagePercentage = 12.5f,
        float rawCpuUsagePercentage = 34.5f,
        long filteredMemoryUsageBytes = 200L,
        long rawMemoryUsageBytes = 300L,
        long filteredAvailableMemoryBytes = 700L,
        long rawAvailableMemoryBytes = 600L,
        long maximumAvailableMemoryBytes = 1_000L) =>
        new(
            filteredCpuUsagePercentage,
            rawCpuUsagePercentage,
            filteredMemoryUsageBytes,
            rawMemoryUsageBytes,
            filteredAvailableMemoryBytes,
            rawAvailableMemoryBytes,
            maximumAvailableMemoryBytes);

    private static GrainTimerCreationOptions CreateGrainTimerCreationOptions(
        TimeSpan? dueTime = null,
        TimeSpan? period = null,
        bool interleave = true,
        bool keepAlive = true) =>
        new(dueTime ?? TimeSpan.FromSeconds(2), period ?? TimeSpan.FromMinutes(3))
        {
            Interleave = interleave,
            KeepAlive = keepAlive,
        };

    private static void AssertEqual(DeactivationReason left, DeactivationReason right)
    {
        AssertEqualContract(left, right);
        Assert.True(left == right);
        Assert.False(left != right);
    }

    private static void AssertNotEqual(DeactivationReason left, DeactivationReason right)
    {
        AssertNotEqualContract(left, right);
        Assert.False(left == right);
        Assert.True(left != right);
    }

    private static void AssertEqual<T>(Immutable<T> left, Immutable<T> right)
    {
        AssertEqualContract(left, right);
        Assert.True(left == right);
        Assert.False(left != right);
    }

    private static void AssertNotEqual<T>(Immutable<T> left, Immutable<T> right)
    {
        AssertNotEqualContract(left, right);
        Assert.False(left == right);
        Assert.True(left != right);
    }

    private static void AssertEqual(EnvironmentStatistics left, EnvironmentStatistics right)
    {
        AssertEqualContract(left, right);
        Assert.True(left == right);
        Assert.False(left != right);
    }

    private static void AssertNotEqual(EnvironmentStatistics left, EnvironmentStatistics right)
    {
        AssertNotEqualContract(left, right);
        Assert.False(left == right);
        Assert.True(left != right);
    }

    private static void AssertEqual(GrainTimerCreationOptions left, GrainTimerCreationOptions right)
    {
        AssertEqualContract(left, right);
        Assert.True(left == right);
        Assert.False(left != right);
    }

    private static void AssertNotEqual(GrainTimerCreationOptions left, GrainTimerCreationOptions right)
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
        Assert.True(right.Equals(left));
        Assert.True(((object)left).Equals(right));
        Assert.True(((object)right).Equals(left));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    private static void AssertNotEqualContract<T>(T left, T right)
        where T : struct, IEquatable<T>
    {
        Assert.NotEqual(left, right);
        Assert.False(left.Equals(right));
        Assert.False(right.Equals(left));
        Assert.False(((object)left).Equals(right));
        Assert.False(((object)right).Equals(left));
    }

    private static void AssertHashCollections<T>(T original, T equal, T unequal)
        where T : notnull
    {
        var set = new HashSet<T> { original };
        var dictionary = new Dictionary<T, string> { [original] = "stored" };

        Assert.Contains(equal, set);
        Assert.DoesNotContain(unequal, set);
        Assert.True(dictionary.TryGetValue(equal, out var value));
        Assert.Equal("stored", value);
        Assert.False(dictionary.ContainsKey(unequal));
    }
}
