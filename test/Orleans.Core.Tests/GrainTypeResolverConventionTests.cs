using Microsoft.Extensions.Options;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.TypeSystem;
using Xunit;

namespace UnitTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public class GrainTypeResolverConventionTests
{
    public static IEnumerable<TheoryDataRow<Type, string>> ConventionCases =>
    [
        new(typeof(MiXeDCaSe), "mixedcase") { TestDisplayName = "GetGrainType_Convention_MixedCaseName" },
        new(typeof(PendingGrAiN), "pending") { TestDisplayName = "GetGrainType_Convention_MixedCaseSuffix" },
        new(typeof(GrainLedger), "grainledger") { TestDisplayName = "GetGrainType_Convention_InteriorSuffixRetained" },
        new(typeof(GrainLedgerGrAiN), "grainledger") { TestDisplayName = "GetGrainType_Convention_OnlyTerminalSuffixTrimmed" },
        new(typeof(Grain), "grain") { TestDisplayName = "GetGrainType_Convention_SuffixOnlyRetained" },
        new(typeof(PendingGrAiN<>), "pending`1") { TestDisplayName = "GetGrainType_Convention_GenericSuffixAndArity" },
    ];

    [Theory, MemberData(nameof(ConventionCases))]
    public void GetGrainType_Convention_PreservesCanonicalName(Type grainClass, string expectedName)
    {
        var converter = new TypeConverter([], [], [], Options.Create(new TypeManifestOptions()), new CachedTypeResolver());
        var resolver = new GrainTypeResolver([], converter);

        var result = resolver.GetGrainType(grainClass);

        Assert.Equal(GrainType.Create(expectedName), result);
    }

    // Plain classes exercise naming without adding generated or serialized grain fixtures.
    private sealed class MiXeDCaSe;
    private sealed class PendingGrAiN;
    private sealed class GrainLedger;
    private sealed class GrainLedgerGrAiN;
    private sealed class Grain;
    private sealed class PendingGrAiN<T>;
}
