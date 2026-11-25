using System.Reflection;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace UnitTests.StorageTests.Relational;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Persistence")]
[TestCategory("BVT")]
public sealed class AdoNetGrainStorageContractTests
{
    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", null, true)]
    [InlineData("", "existing", true)]
    [InlineData(null, "existing", true)]
    [InlineData("same", "same", true)]
    [InlineData("updated", null, false)]
    [InlineData("updated", "existing", false)]
    public void VersionConflictPreservesNullEmptyAndUpdatedDistinctions(
        string? storageVersion, string? grainVersion, bool conflictExpected)
    {
        var method = typeof(AdoNetGrainStorage).GetMethod("CheckVersionInconsistency", BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = method.Invoke(null, ["write", "service", "provider", storageVersion, grainVersion, "grain-type", "42"]);

        if (conflictExpected)
        {
            var exception = Assert.IsType<InconsistentStateException>(result);
            Assert.Contains(
                $"Version conflict (write): ServiceId=service ProviderName=provider GrainType=grain-type GrainId=42 ETag={grainVersion}.",
                exception.Message);
        }
        else
        {
            Assert.Null(result);
        }
    }

    [Theory]
    [InlineData("Plain", "Plain")]
    [InlineData("Plain, Assembly", "Plain, Assembly")]
    [InlineData("Sample`1[[Item, Assembly, Version=1.0.0.0]]", "Sample`1[[Item, Assembly]]")]
    [InlineData(
        "Sample`2[[Item, Assembly, Version=1.0.0.0],[Other, OtherAssembly, Version=2.0.0.0]]",
        "Sample`2[[Item, Assembly],[Other, OtherAssembly]]")]
    public void GenericTypeNormalizationPreservesExistingStoredNames(string input, string expected)
    {
        var method = typeof(AdoNetGrainStorage).GetMethod("ExtractBaseClass", BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = method.Invoke(null, [input]);

        Assert.Equal(expected, Assert.IsType<string>(result));
    }
}
