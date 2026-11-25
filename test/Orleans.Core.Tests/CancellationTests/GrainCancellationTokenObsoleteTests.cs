using System.Reflection;
using Orleans;
using Xunit;

namespace UnitTests.CancellationTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class GrainCancellationTokenObsoleteTests
{
    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public void LegacyCancellationTypesAreObsolete()
    {
        var assembly = typeof(Grain).Assembly;
        var token = assembly.GetType("Orleans.GrainCancellationToken", throwOnError: true)!;
        var tokenSource = assembly.GetType("Orleans.GrainCancellationTokenSource", throwOnError: true)!;

        var tokenObsolete = Assert.IsType<ObsoleteAttribute>(token.GetCustomAttribute<ObsoleteAttribute>());
        var tokenSourceObsolete = Assert.IsType<ObsoleteAttribute>(tokenSource.GetCustomAttribute<ObsoleteAttribute>());

        Assert.Equal("Use CancellationToken instead.", tokenObsolete.Message);
        Assert.False(tokenObsolete.IsError);
        Assert.Equal("Use CancellationTokenSource instead.", tokenSourceObsolete.Message);
        Assert.False(tokenSourceObsolete.IsError);
    }
}
