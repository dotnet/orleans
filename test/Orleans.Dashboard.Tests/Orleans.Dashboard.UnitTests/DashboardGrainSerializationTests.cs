using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orleans.Dashboard;
using Orleans.Dashboard.Implementation.Grains;
using Orleans.Serialization.Configuration;
using Xunit;

namespace UnitTests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Dashboard")]
public class DashboardGrainSerializationTests
{
    private const string ExpectedUnknownGrainPayload = """
        {
          "error": "Unknown grain type \u0027UnitTests.UnknownGrain\u0027. (Parameter \u0027grainType\u0027)"
        }
        """;

    [Fact]
    public async Task GetGrainState_UnknownGrainType_PreservesIndentedJsonPayload()
    {
        var grain = CreateGrain();

        var result = await grain.GetGrainState(
            "42",
            "UnitTests.UnknownGrain",
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ExpectedUnknownGrainPayload.ReplaceLineEndings("\n"),
            result.Value.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task GetGrainState_ConcurrentCalls_ReuseReadOnlySerializerOptions()
    {
        var serializerOptionsField = Assert.IsAssignableFrom<FieldInfo>(
            typeof(DashboardGrain).GetField(
                "GrainStateSerializerOptions",
                BindingFlags.NonPublic | BindingFlags.Static));
        var serializerOptions = Assert.IsType<JsonSerializerOptions>(serializerOptionsField.GetValue(null));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, 32).Select(async _ =>
        {
            await start.Task;
            return await Task.Run(
                () => CreateGrain().GetGrainState(
                    "42",
                    "UnitTests.UnknownGrain",
                    TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken);
        }).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.All(
            results,
            result => Assert.Equal(
                ExpectedUnknownGrainPayload.ReplaceLineEndings("\n"),
                result.Value.ReplaceLineEndings("\n")));
        Assert.True(serializerOptions.IsReadOnly);
    }

    private static DashboardGrain CreateGrain() => new(
        Options.Create(new DashboardOptions()),
        Options.Create(new GrainProfilerOptions()),
        Options.Create(new TypeManifestOptions()),
        siloDetailsProvider: null!,
        siloGrainClient: null!);
}
