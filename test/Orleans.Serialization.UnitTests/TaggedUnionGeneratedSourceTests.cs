using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static VerifyXunit.Verifier;

namespace Orleans.Serialization.UnitTests.TaggedUnions;

[Trait("Category", "BVT")]
[Trait("Suite", "BVT")]
[Trait("Provider", "None")]
[Trait("Area", "Serialization")]
public class TaggedUnionGeneratedSourceTests
{
    [Theory]
    [InlineData("Sensor")]
    [InlineData("SensorUnion")]
    [InlineData("Shape")]
    [InlineData("Pet")]
    [InlineData("IntOrString")]
    [InlineData("Either")]
    [InlineData("ImmutablePet")]
    [InlineData("CallbackPet")]
    [InlineData("CallbackNumber")]
    [InlineData("ImmutableCallbackPet")]
    [InlineData("CustomAccessUnion")]
    public Task GeneratedSourceMatchesSnapshot(string typeName)
    {
        // Capture the SDK compiler's output: the test-host Roslyn version does not expose union case metadata.
        var directory = Path.Combine(AppContext.BaseDirectory, "TaggedUnionGeneratedSources");
        var sources = Directory.GetFiles(directory, $"*.TaggedUnions.{typeName}*.g.cs").Select(File.ReadAllText);
        var source = Assert.Single(sources, text =>
            text.Contains($"class Codec_{typeName} :", StringComparison.Ordinal)
            || text.Contains($"class Codec_{typeName}<", StringComparison.Ordinal));

        return Verify(source, extension: "cs").UseDirectory("snapshots").UseParameters(typeName);
    }
}
