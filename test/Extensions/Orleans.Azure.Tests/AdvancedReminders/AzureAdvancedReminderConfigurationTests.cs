using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
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

    [Fact]
    public void SiloBuilderConnectionStringOverload_ConfiguresTableAndBlobClients()
    {
        var builder = new TestSiloBuilder();

        builder.UseAzureTableAdvancedReminderService("UseDevelopmentStorage=true");

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var reminderOptions = serviceProvider.GetRequiredService<IOptions<AzureTableReminderStorageOptions>>().Value;
        var jobOptions = serviceProvider.GetRequiredService<IOptions<AzureBlobJournalStorageOptions>>().Value;

        Assert.NotNull(reminderOptions.TableServiceClient);
        Assert.NotNull(reminderOptions.BlobServiceClient);
        Assert.Same(reminderOptions.BlobServiceClient, jobOptions.BlobServiceClient);
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}

#pragma warning restore ORLEANSEXP005
