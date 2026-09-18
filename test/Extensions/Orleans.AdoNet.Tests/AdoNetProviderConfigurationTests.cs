using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Tests.SqlUtils;
using TestExtensions;

namespace UnitTests.AdoNet;

[TestCategory("AdoNet")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Persistence")]
public sealed class AdoNetProviderConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("direct-connection")]
    public void GetConnectionString_ConflictingReferences_IdentifiesSectionAndSettings(string? directConnection)
    {
        var configuration = CreateConfiguration("first", "second", directConnection);
        using var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).BuildServiceProvider();

        var exception = Assert.Throws<OrleansConfigurationException>(() =>
            AdoNetProviderConfiguration.GetConnectionString(configuration.GetSection("Provider"), services));

        Assert.Contains("Provider", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ServiceKey", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ConnectionName", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("orders", "orders", null, "named-connection")]
    [InlineData("orders", "ORDERS", null, "named-connection")]
    [InlineData("orders", null, null, "named-connection")]
    [InlineData(null, "orders", null, "named-connection")]
    [InlineData(" ", "orders", null, "named-connection")]
    [InlineData("orders", "orders", "direct-connection", "direct-connection")]
    [InlineData(null, null, "direct-connection", "direct-connection")]
    [InlineData(null, null, null, null)]
    public void GetConnectionString_CompatibleReferences_PreservesResolution(
        string? serviceKey,
        string? connectionName,
        string? directConnection,
        string? expected)
    {
        var configuration = CreateConfiguration(serviceKey, connectionName, directConnection);
        using var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).BuildServiceProvider();

        var result = AdoNetProviderConfiguration.GetConnectionString(configuration.GetSection("Provider"), services);

        Assert.Equal(expected, result);
    }

    private static IConfigurationRoot CreateConfiguration(string? serviceKey, string? connectionName, string? directConnection)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Provider:ServiceKey"] = serviceKey,
            ["Provider:ConnectionName"] = connectionName,
            ["Provider:ConnectionString"] = directConnection,
            ["ConnectionStrings:orders"] = "named-connection",
            ["ConnectionStrings:first"] = "first-connection",
            ["ConnectionStrings:second"] = "second-connection",
        }).Build();
}
