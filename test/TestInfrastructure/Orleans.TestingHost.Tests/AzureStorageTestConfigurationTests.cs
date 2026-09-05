using TestExtensions;
using Tester;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
public class AzureStorageTestConfigurationTests
{
    [Fact]
    public void ConnectionStringAccessRejectsAadAuthentication()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TestDefaultConfiguration.GetAzureStorageConnectionString(
                useAadAuthentication: true,
                dataConnectionString: null,
                static () => throw new InvalidOperationException("Azurite should not be used.")));

        Assert.Contains("unavailable when AAD authentication is enabled", exception.Message);
    }

    [Fact]
    public void ConnectionStringAccessUsesConfiguredConnectionString()
    {
        var result = TestDefaultConfiguration.GetAzureStorageConnectionString(
            useAadAuthentication: false,
            dataConnectionString: "configured-connection",
            static () => throw new InvalidOperationException("Azurite should not be used."));

        Assert.Equal("configured-connection", result);
    }

    [Fact]
    public void ConnectionStringAccessUsesAzuriteWhenUnconfigured()
    {
        var result = TestDefaultConfiguration.GetAzureStorageConnectionString(
            useAadAuthentication: false,
            dataConnectionString: null,
            static () => "azurite-connection");

        Assert.Equal("azurite-connection", result);
    }

    [Fact]
    public void AadConfigurationRequiresEveryStorageEndpoint()
    {
        var error = TestUtils.GetAzureStorageAadConfigurationError(
            "https://account.table.core.windows.net",
            "not-an-absolute-uri",
            null);

        Assert.NotNull(error);
        Assert.DoesNotContain(nameof(TestDefaultConfiguration.TableEndpoint), error);
        Assert.Contains(nameof(TestDefaultConfiguration.DataBlobUri), error);
        Assert.Contains(nameof(TestDefaultConfiguration.DataQueueUri), error);
    }

    [Fact]
    public void AadConfigurationAcceptsValidStorageEndpoints()
    {
        var error = TestUtils.GetAzureStorageAadConfigurationError(
            "https://account.table.core.windows.net",
            "https://account.blob.core.windows.net",
            "https://account.queue.core.windows.net");

        Assert.Null(error);
    }
}
