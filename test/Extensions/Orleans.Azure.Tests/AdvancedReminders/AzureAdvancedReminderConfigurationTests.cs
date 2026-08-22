using System.Linq;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.AdvancedReminders.AzureStorage;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.AdvancedRemindersTest;

#pragma warning disable ORLEANSEXP005

[TestCategory("Reminders"), TestCategory("AzureStorage")]
public class AzureAdvancedReminderConfigurationTests
{
    [Fact]
    public void ReminderRowKeys_AreCollisionFreeForPreviouslySanitizedNames()
    {
        var grainId = GrainId.Create("test", "row-key");

        var slash = ReminderTableEntry.ConstructRowKey(grainId, "a/b");
        var underscore = ReminderTableEntry.ConstructRowKey(grainId, "a_b");

        Assert.NotEqual(slash, underscore);
        Assert.DoesNotContain('/', slash);
        Assert.DoesNotContain('/', underscore);
    }

    [Fact]
    public void ReminderPartitionKeyBounds_DoNotIncludeAnotherEncodedServicePrefix()
    {
        const string serviceId = "service";
        var (lower, upper) = ReminderTableEntry.ConstructPartitionKeyBounds(serviceId);
        var ownKey = ReminderTableEntry.ConstructPartitionKey(serviceId, 42);

        Assert.EndsWith("!", lower, StringComparison.Ordinal);
        Assert.Equal(lower[..^1] + '"', upper);
        Assert.True(string.CompareOrdinal(ownKey, lower) > 0);
        Assert.True(string.CompareOrdinal(ownKey, upper) < 0);

        var otherServiceKey = ReminderTableEntry.ConstructPartitionKey("service?", 42);
        Assert.False(string.CompareOrdinal(otherServiceKey, lower) > 0
            && string.CompareOrdinal(otherServiceKey, upper) < 0);
    }

    [Fact]
    public void ConvertFromTableEntryList_SkipsMalformedGrainIdAndReturnsHealthyRows()
    {
        const string serviceId = "service";
        var now = DateTime.UtcNow;
        var options = new AzureTableReminderStorageOptions
        {
            TableServiceClient = new TableServiceClient("UseDevelopmentStorage=true"),
            BlobServiceClient = new BlobServiceClient(new Uri("https://example.blob.core.windows.net")),
        };
        var table = new AzureBasedReminderTable(
            NullLoggerFactory.Instance,
            Options.Create(new ClusterOptions { ServiceId = serviceId }),
            Options.Create(options));
        var healthyGrainId = GrainId.Create("test", "healthy");
        var entries = new List<(ReminderTableEntry Entity, string ETag)>
        {
            (CreateEntry(string.Empty), "etag-malformed"),
            (CreateEntry(healthyGrainId.ToString()), "etag-healthy"),
        };

        var result = table.ConvertFromTableEntryList(entries);

        var reminder = Assert.Single(result.Reminders);
        Assert.Equal(healthyGrainId, reminder.GrainId);

        ReminderTableEntry CreateEntry(string grainReference) => new()
        {
            GrainReference = grainReference,
            ReminderName = "reminder",
            ServiceId = serviceId,
            StartAt = LogFormatter.PrintDate(now),
            Period = TimeSpan.FromMinutes(1).ToString("c"),
        };
    }

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
    public void ActionOverload_RegistersAndRunsStorageOptionsValidator()
    {
        var services = new ServiceCollection();
        services.UseAzureTableAdvancedReminderService(options =>
        {
            options.TableServiceClient = new TableServiceClient("UseDevelopmentStorage=true");
            options.BlobServiceClient = new BlobServiceClient(new Uri("https://example.blob.core.windows.net"));
        });

        var validatorDescriptor = services.Last(service => service.ServiceType == typeof(IConfigurationValidator));
        using var serviceProvider = services.BuildServiceProvider();
        var validator = Assert.IsType<AzureTableReminderStorageOptionsValidator>(
            validatorDescriptor.ImplementationFactory!(serviceProvider));

        validator.ValidateConfiguration();
    }

    [Fact]
    public void StorageOptionsValidator_RequiresBlobServiceClient()
    {
        var options = new AzureTableReminderStorageOptions
        {
            TableServiceClient = new TableServiceClient("UseDevelopmentStorage=true"),
            BlobServiceClient = null!,
        };
        var validator = new AzureTableReminderStorageOptionsValidator(options, Options.DefaultName);

        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);

        Assert.Contains(nameof(options.BlobServiceClient), exception.Message);
    }

    [Fact]
    public void StorageOptionsValidator_RequiresJobContainerName()
    {
        var options = new AzureTableReminderStorageOptions
        {
            TableServiceClient = new TableServiceClient("UseDevelopmentStorage=true"),
            BlobServiceClient = new BlobServiceClient(new Uri("https://example.blob.core.windows.net")),
            JobContainerName = " ",
        };
        var validator = new AzureTableReminderStorageOptionsValidator(options, Options.DefaultName);

        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);

        Assert.Contains(nameof(options.JobContainerName), exception.Message);
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
