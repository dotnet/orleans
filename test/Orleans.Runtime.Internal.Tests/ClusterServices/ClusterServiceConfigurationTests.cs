using Orleans.Runtime.ClusterServices;
using TestExtensions;
using Xunit;

namespace UnitTests.ClusterServices;

[TestArea("Runtime")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
public sealed class ClusterServiceConfigurationTests
{
    private const string AssignmentStrategy = "uniform-hash-ring/v1";

    [Fact]
    public void Constructor_PreservesAssignmentInputs()
    {
        var configuration = new ClusterServiceConfiguration("orders", 31, AssignmentStrategy);

        Assert.Equal("orders", configuration.ServiceId);
        Assert.Equal(31, configuration.PartitionsPerSilo);
        Assert.Equal(AssignmentStrategy, configuration.AssignmentStrategy);
    }

    [Fact]
    public void Constructor_RejectsNullServiceId()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new ClusterServiceConfiguration(null!, 1, AssignmentStrategy));

        Assert.Equal("serviceId", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void Constructor_RejectsEmptyOrWhitespaceServiceId(string serviceId)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new ClusterServiceConfiguration(serviceId, 1, AssignmentStrategy));

        Assert.Equal("serviceId", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Constructor_RejectsNonPositivePartitionsPerSilo(int partitionsPerSilo)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ClusterServiceConfiguration("test-service", partitionsPerSilo, AssignmentStrategy));

        Assert.Equal("partitionsPerSilo", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsNullAssignmentStrategy()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new ClusterServiceConfiguration("test-service", 1, null!));

        Assert.Equal("assignmentStrategy", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void Constructor_RejectsEmptyOrWhitespaceAssignmentStrategy(string assignmentStrategy)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new ClusterServiceConfiguration("test-service", 1, assignmentStrategy));

        Assert.Equal("assignmentStrategy", exception.ParamName);
    }
}
