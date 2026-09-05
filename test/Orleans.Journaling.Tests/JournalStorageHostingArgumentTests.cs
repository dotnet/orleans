using Orleans.Journaling;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
[TestArea("Journaling")]
public sealed class JournalStorageHostingArgumentTests
{
    [Fact]
    public void AddAzureBlobJournalStorage_NullBuilder_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => AzureBlobStorageHostingExtensions.AddAzureBlobJournalStorage(null!));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void AddAzureTableJournalStorage_NullBuilder_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => AzureTableStorageHostingExtensions.AddAzureTableJournalStorage(null!));

        Assert.Equal("builder", exception.ParamName);
    }

}
