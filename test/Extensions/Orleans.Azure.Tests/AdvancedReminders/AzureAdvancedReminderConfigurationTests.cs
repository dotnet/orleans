using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.AdvancedReminders.AzureStorage;
using Orleans.Hosting;
using Orleans.Journaling;
using TestExtensions;
using Xunit;

namespace UnitTests.AdvancedRemindersTest;

#pragma warning disable ORLEANSEXP005

[TestCategory("Reminders"), TestCategory("AzureStorage")]
public class AzureAdvancedReminderConfigurationTests
{
    [Fact]
    public void UseAzureTableAdvancedReminderService_ConfiguresDurableJobStorage()
    {
        var blobServiceClient = new BlobServiceClient(new Uri("https://example.blob.core.windows.net"));
        var services = new ServiceCollection();

        services.UseAzureTableAdvancedReminderService(options =>
        {
            options.BlobServiceClient = blobServiceClient;
            options.JobContainerName = "advanced-reminder-jobs-test";
        });

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<AzureBlobJournalStorageOptions>>().Value;

        Assert.Same(blobServiceClient, options.BlobServiceClient);
        Assert.Equal("advanced-reminder-jobs-test", options.ContainerName);
    }
}

#pragma warning restore ORLEANSEXP005
