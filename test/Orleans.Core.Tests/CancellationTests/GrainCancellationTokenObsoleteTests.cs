using System.Reflection;
using Orleans;
using Xunit;

namespace UnitTests.CancellationTests;

public class GrainCancellationTokenObsoleteTests
{
    [Fact]
    public void LegacyCancellationTypesAreObsolete()
    {
        var assembly = typeof(Grain).Assembly;
        var token = assembly.GetType("Orleans.GrainCancellationToken", throwOnError: true)!;
        var tokenSource = assembly.GetType("Orleans.GrainCancellationTokenSource", throwOnError: true)!;

        Assert.Equal(
            "Use CancellationToken instead.",
            Assert.IsType<ObsoleteAttribute>(token.GetCustomAttribute<ObsoleteAttribute>()).Message);
        Assert.Equal(
            "Use CancellationTokenSource instead.",
            Assert.IsType<ObsoleteAttribute>(tokenSource.GetCustomAttribute<ObsoleteAttribute>()).Message);
    }
}
