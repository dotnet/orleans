using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Serialization;
using Orleans.Serialization.TypeSystem;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Serializable types in Orleans.Durable carry aliases, so the type names which the serializer writes to journals and job
/// storage omit the declaring assembly.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class DurableTypeIdentityTests
{
    [Fact]
    public void EverySerializableTypeHasAnAlias()
    {
        var unaliased = typeof(IDurableStateManager).Assembly.GetTypes()
            .Where(type => type.IsDefined(typeof(GenerateSerializerAttribute), inherit: false)
                && !type.IsDefined(typeof(AliasAttribute), inherit: false))
            .Select(type => type.FullName)
            .Order(StringComparer.Ordinal);

        Assert.Empty(unaliased);
    }

    [Theory]
    [InlineData(typeof(ValueChange<int>), "Orleans.Journaling.ValueChange`1[[int]]")]
    [InlineData(typeof(JournaledStatePreCommitException), "Orleans.Journaling.JournaledStatePreCommitException")]
    [InlineData(typeof(DurableJob), "Orleans.DurableJobs.DurableJob")]
    public void FormattedNameIsTheAlias(Type type, string expected)
    {
        var converter = CreateTypeConverter();

        Assert.Equal(expected, converter.Format(type));
        Assert.True(converter.TryParse(expected, out var parsed));
        Assert.Equal(type, parsed);
    }

    private static TypeConverter CreateTypeConverter()
        => new ServiceCollection().AddSerializer().BuildServiceProvider().GetRequiredService<TypeConverter>();
}
