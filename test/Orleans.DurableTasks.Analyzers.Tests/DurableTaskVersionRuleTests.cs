using Xunit;

namespace Orleans.DurableTasks.Analyzers.Tests;

/// <summary>Tests the rule for version checks.</summary>
[TestCategory("BVT"), TestCategory("Analyzer")]
public class DurableTaskVersionRuleTests
{
    [Fact]
    public Task VersionChecksPassConstantNamesAndRanges() => AnalyzerVerifier.VerifyAsync("""
        public class C
        {
            private const string Carrier = "carrier";
            private const int CarrierVersion = 2;

            private static async DurableTask<int> Step(int value) => value;

            public async DurableTask<int> RunAsync(string region, int newest)
            {
                var constant = DurableTask.GetVersion("change", 0, 1);
                var named = DurableTask.GetVersion(Carrier, minSupported: 1, maxSupported: CarrierVersion);
                var derived = DurableTask.GetVersion({|ORLEANS0118:$"carrier-{region}"|}, 0, 1);
                var ranged = DurableTask.GetVersion("change", 0, {|ORLEANS0118:newest|});
                return await Step(constant + named + derived + ranged).WithId("step");
            }
        }
        """);

    [Fact]
    public Task VersionDescriptorsDeclareConstantNamesAndRanges() => AnalyzerVerifier.VerifyAsync("""
        public class C
        {
            private static readonly DurableVersion CarrierApi = new("carrier-api", minSupported: 0, maxSupported: 1);

            private static async DurableTask<int> Step(int value) => value;

            public async DurableTask<int> RunAsync(string region, int newest)
            {
                var regional = new DurableVersion({|ORLEANS0118:"carrier-" + region|}, 0, {|ORLEANS0118:newest|});
                var version = CarrierApi.IsAtLeast(1) ? 1 : 0;
                return await Step(version + regional.Get()).WithId("step");
            }
        }
        """);
}
