using Xunit;

namespace Tester.AzureUtils;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Persistence")]
public class StorageEmulatorUtilitiesTests
{
    [Fact]
    public void DefaultAzuriteIsRejectedWithoutResolvingConnectionString()
    {
        var reason = StorageEmulatorUtilities.GetUnsupportedEmulatorSkipReason(
            useAadAuthentication: false,
            useAzurite: true,
            dataConnectionString: null);

        Assert.Equal("This test does not support Azurite.", reason);
    }

    [Fact]
    public void ExplicitCloudStorageIsAccepted()
    {
        var reason = StorageEmulatorUtilities.GetUnsupportedEmulatorSkipReason(
            useAadAuthentication: false,
            useAzurite: false,
            dataConnectionString: "DefaultEndpointsProtocol=https;AccountName=account");

        Assert.Null(reason);
    }
}
