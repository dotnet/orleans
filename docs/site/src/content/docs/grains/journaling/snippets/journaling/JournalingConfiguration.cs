using System.Text.Json.Serialization;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.Json;

namespace Orleans.Docs.Snippets.Journaling;

[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ulong))]
internal partial class JournalJsonContext : JsonSerializerContext;

public static class JournalingConfiguration
{
    public static IHost ConfigureJson()
    {
        // <configure_json_format>
        var builder = Host.CreateApplicationBuilder();
        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder
                .AddAzureBlobJournalStorage(options =>
                    options.ConfigureBlobServiceClient("UseDevelopmentStorage=true"))
                .UseJsonJournalFormat(JournalJsonContext.Default);
        });

        var host = builder.Build();
        // </configure_json_format>
        return host;
    }

    public static IHost ConfigureBinary()
    {
        // <configure_binary_format>
        var builder = Host.CreateApplicationBuilder();
        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.AddAzureBlobJournalStorage(options =>
                options.ConfigureBlobServiceClient("UseDevelopmentStorage=true"));
            siloBuilder.Services.Configure<JournaledStateManagerOptions>(options =>
                options.JournalFormatKey = "orleans-binary");
        });

        var host = builder.Build();
        // </configure_binary_format>
        return host;
    }

    public static IHost ConfigureRetirement()
    {
        // <configure_retirement>
        var builder = Host.CreateApplicationBuilder();
        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.AddAzureBlobJournalStorage(options =>
                options.ConfigureBlobServiceClient("UseDevelopmentStorage=true"));
            siloBuilder.Services.Configure<JournaledStateManagerOptions>(options =>
                options.RetirementGracePeriod = TimeSpan.FromDays(14));
        });

        var host = builder.Build();
        // </configure_retirement>
        return host;
    }

    public static IHost ConfigureAzureBlob(BlobServiceClient blobServiceClient)
    {
        // <configure_azure_blob>
        var builder = Host.CreateApplicationBuilder();
        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.AddAzureBlobJournalStorage(options =>
            {
                options.BlobServiceClient = blobServiceClient;
                options.ContainerName = "journals";
                options.GetWalBlobName =
                    journalId => $"orders/{journalId.Value}/wal";
                options.GetCheckpointBlobName =
                    (journalId, snapshotId) =>
                        $"orders/{journalId.Value}/chk.{snapshotId}";
            });
        });

        var host = builder.Build();
        // </configure_azure_blob>
        return host;
    }

    public static IHost ConfigureAzureTable(TableServiceClient tableServiceClient)
    {
        // <configure_azure_table>
        var builder = Host.CreateApplicationBuilder();
        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.AddAzureTableJournalStorage(options =>
            {
                options.TableServiceClient = tableServiceClient;
                options.TableName = "journal";
                options.CompactionRowCountThreshold = 10_000;
                options.CompactionSizeThreshold = 32 * 1024 * 1024;
            });
        });

        var host = builder.Build();
        // </configure_azure_table>
        return host;
    }
}
