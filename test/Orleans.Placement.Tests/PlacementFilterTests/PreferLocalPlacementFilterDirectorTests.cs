using System.Net;
using Orleans.Runtime.Placement.Filtering;
using Xunit;

namespace UnitTests.PlacementFilterTests;

/// <summary>
/// Tests for prefer local placement filter director behavior.
/// </summary>
[TestCategory("Placement"), TestCategory("Filters")]
public class PreferLocalPlacementFilterDirectorTests
{
    private static SiloAddress CreateSiloAddress(int port) =>
        SiloAddress.New(IPAddress.Loopback, port, 1);

    private static PreferLocalPlacementFilterDirector CreateDirector(SiloAddress localSiloAddress) =>
        new(new TestLocalSiloDetails("name", "clusterId", "dnsHostName", localSiloAddress, localSiloAddress));

    [Fact, TestCategory("Functional")]
    public void PreferLocalPlacementFilterDirector_CanBeCreated()
    {
        var director = CreateDirector(CreateSiloAddress(1000));
        Assert.NotNull(director);
    }

    [Fact, TestCategory("Functional")]
    public void PreferLocalPlacementFilterDirector_ReturnsOnlyLocalSiloWhenPresent()
    {
        var localSiloAddress = CreateSiloAddress(1000);
        var remoteSilo1 = CreateSiloAddress(1001);
        var remoteSilo2 = CreateSiloAddress(1002);

        var director = CreateDirector(localSiloAddress);

        var result = director.Filter(
            new PreferLocalPlacementFilterStrategy(),
            default,
            [remoteSilo1, localSiloAddress, remoteSilo2]).ToList();

        Assert.Single(result);
        Assert.Equal(localSiloAddress, result[0]);
    }

    [Fact, TestCategory("Functional")]
    public void PreferLocalPlacementFilterDirector_ReturnsAllSilosWhenLocalAbsent()
    {
        var localSiloAddress = CreateSiloAddress(1000);
        var remoteSilo1 = CreateSiloAddress(1001);
        var remoteSilo2 = CreateSiloAddress(1002);

        var director = CreateDirector(localSiloAddress);

        var result = director.Filter(
            new PreferLocalPlacementFilterStrategy(),
            default,
            [remoteSilo1, remoteSilo2]).ToList();

        Assert.Equal(2, result.Count);
        Assert.Contains(remoteSilo1, result);
        Assert.Contains(remoteSilo2, result);
    }

    [Fact, TestCategory("Functional")]
    public void PreferLocalPlacementFilterDirector_ReturnsEmptyWhenNoCandidates()
    {
        var director = CreateDirector(CreateSiloAddress(1000));

        var result = director.Filter(
            new PreferLocalPlacementFilterStrategy(),
            default,
            []).ToList();

        Assert.Empty(result);
    }

    [Fact, TestCategory("Functional")]
    public void PreferLocalPlacementFilterDirector_ReturnsLocalWhenOnlyCandidate()
    {
        var localSiloAddress = CreateSiloAddress(1000);

        var director = CreateDirector(localSiloAddress);

        var result = director.Filter(
            new PreferLocalPlacementFilterStrategy(),
            default,
            [localSiloAddress]).ToList();

        Assert.Single(result);
        Assert.Equal(localSiloAddress, result[0]);
    }
}
