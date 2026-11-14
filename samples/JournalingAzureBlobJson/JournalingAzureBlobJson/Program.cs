using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Journaling;
using Orleans.Journaling.Json;

namespace JournalingAzureBlobJson;

internal static class Program
{
    private const string ConnectionStringEnvironmentVariable = "ORLEANS_AZURE_STORAGE_CONNECTION_STRING";
    private const string AzureWebJobsStorageEnvironmentVariable = "AzureWebJobsStorage";
    private const string AspireBlobConnectionName = "blobs";
    private const string ContainerEnvironmentVariable = "ORLEANS_JOURNALING_CONTAINER";
    private const string BlobEnvironmentVariable = "ORLEANS_JOURNALING_BLOB";
    private const string DefaultContainerName = "orleans-journaling-json-sample";
    private const string DefaultBlobName = "journaled-json-sample.jsonl";

    public static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.AddConsole();

        var settings = SampleSettings.Parse(args, builder.Configuration.GetConnectionString(AspireBlobConnectionName));
        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            Console.Error.WriteLine($"Run the Aspire AppHost, set {ConnectionStringEnvironmentVariable}, set {AzureWebJobsStorageEnvironmentVariable}, or pass --connection-string <value>.");
            return 1;
        }

        var blobServiceClient = new BlobServiceClient(settings.ConnectionString);
        var container = blobServiceClient.GetBlobContainerClient(settings.ContainerName);

        var walBlobName = $"{settings.BlobName}/wal";
        var blob = container.GetAppendBlobClient(walBlobName);

        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder
                .UseLocalhostClustering()
                .Configure<ClusterOptions>(options =>
                {
                    options.ClusterId = "journaling-azure-blob-json-sample";
                    options.ServiceId = "journaling-azure-blob-json-sample";
                })
                .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                .AddAzureBlobJournalStorage(options =>
                {
                    options.BlobServiceClient = blobServiceClient;
                    options.ContainerName = settings.ContainerName;
                    options.GetWalBlobName = _ => walBlobName;
                    options.GetCheckpointBlobName = (_, snapshotId) => $"{settings.BlobName}/chk.{snapshotId}";
                })
                .UseJsonJournalFormat(JournalingSampleJsonContext.Default);
        });

        using var host = builder.Build();
        var cancellationToken = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

        try
        {
            await host.StartAsync(cancellationToken);
            await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            if (settings.ResetBlob)
            {
                await blob.DeleteIfExistsAsync(cancellationToken: cancellationToken);
            }

            var client = host.Services.GetRequiredService<IClusterClient>();
            var grain = client.GetGrain<IJournaledSampleGrain>("azure-json-codec");

            Console.WriteLine("Writing durable grain state...");
            var written = await grain.RunScenario(cancellationToken);
            Console.WriteLine(JsonSerializer.Serialize(written, JournalingSampleJsonContext.Default.JournaledSampleSummary));

            Console.WriteLine("Deactivating and reactivating the grain to force recovery...");
            await grain.Deactivate(cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);

            var recovered = await grain.GetSummary(cancellationToken);
            ValidateRecovery(written, recovered);
            Console.WriteLine(JsonSerializer.Serialize(recovered, JournalingSampleJsonContext.Default.JournaledSampleSummary));

            Console.WriteLine();
            Console.WriteLine($"Raw JSONL blob contents from {settings.ContainerName}/{walBlobName}:");
            Console.WriteLine(await DownloadBlobAsText(blob, cancellationToken));
            return 0;
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(
                host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
            await host.StopAsync(shutdown.Token);
        }
    }

    private static async Task<string> DownloadBlobAsText(AppendBlobClient blob, CancellationToken cancellationToken)
    {
        var result = await blob.DownloadContentAsync(cancellationToken);
        return Encoding.UTF8.GetString(result.Value.Content.ToArray());
    }

    private static void ValidateRecovery(JournaledSampleSummary written, JournaledSampleSummary recovered)
    {
        if (written.ActivationId == recovered.ActivationId)
        {
            throw new InvalidOperationException("The grain did not reactivate after DeactivateOnIdle.");
        }

        EnsureEqual("inventory", written.Inventory, recovered.Inventory, InventoryEntryEquals);
        EnsureEqual("events", written.Events, recovered.Events, JournalEventEquals);
        EnsureEqual("work queue", written.WorkQueue, recovered.WorkQueue, WorkItemEquals);
        EnsureEqual("tags", written.Tags, recovered.Tags, static (left, right) => string.Equals(left, right, StringComparison.Ordinal));
        EnsureEqual("balance", written.Balance, recovered.Balance, AccountBalanceEquals);
        EnsureEqual("profile", written.Profile, recovered.Profile, ProfileStateEquals);
        EnsureEqual("completion status", written.CompletionStatus, recovered.CompletionStatus, static (left, right) => left == right);
        EnsureEqual("receipt", written.Receipt, recovered.Receipt, static (left, right) => left == right);
    }

    private static void EnsureEqual<T>(string name, IReadOnlyList<T> written, IReadOnlyList<T> recovered, Func<T, T, bool> equals)
    {
        if (written.Count != recovered.Count)
        {
            throw new InvalidOperationException($"Recovered {name} count does not match. Written: {written.Count}, recovered: {recovered.Count}.");
        }

        for (var i = 0; i < written.Count; i++)
        {
            if (!equals(written[i], recovered[i]))
            {
                throw new InvalidOperationException($"Recovered {name} item {i} does not match. Written: {Serialize(written[i])}, recovered: {Serialize(recovered[i])}.");
            }
        }
    }

    private static void EnsureEqual<T>(string name, T written, T recovered, Func<T, T, bool> equals)
    {
        if (!equals(written, recovered))
        {
            throw new InvalidOperationException($"Recovered {name} does not match. Written: {Serialize(written)}, recovered: {Serialize(recovered)}.");
        }
    }

    private static bool InventoryEntryEquals(InventoryEntry left, InventoryEntry right)
        => string.Equals(left.Key, right.Key, StringComparison.Ordinal)
            && InventoryItemEquals(left.Value, right.Value);

    private static bool InventoryItemEquals(InventoryItem left, InventoryItem right)
        => string.Equals(left.Sku, right.Sku, StringComparison.Ordinal)
            && left.Quantity == right.Quantity
            && left.UnitPrice == right.UnitPrice
            && left.Attributes.SequenceEqual(right.Attributes, StringComparer.Ordinal);

    private static bool JournalEventEquals(JournalEvent left, JournalEvent right)
        => string.Equals(left.EventId, right.EventId, StringComparison.Ordinal)
            && left.Timestamp == right.Timestamp
            && string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
            && left.Notes.SequenceEqual(right.Notes, StringComparer.Ordinal);

    private static bool WorkItemEquals(WorkItem left, WorkItem right)
        => left.WorkId == right.WorkId
            && string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
            && left.Priority == right.Priority
            && left.Urgent == right.Urgent;

    private static bool AccountBalanceEquals(AccountBalance left, AccountBalance right)
        => string.Equals(left.Currency, right.Currency, StringComparison.Ordinal)
            && left.Amount == right.Amount
            && left.Ledger.SequenceEqual(right.Ledger);

    private static bool ProfileStateEquals(ProfileState left, ProfileState right)
        => string.Equals(left.Owner, right.Owner, StringComparison.Ordinal)
            && left.Revision == right.Revision
            && left.UpdatedAt == right.UpdatedAt
            && AccountBalanceEquals(left.LastBalance, right.LastBalance);

    private static string Serialize<T>(T value)
    {
        if (value is null)
        {
            return "null";
        }

        return JsonSerializer.Serialize(value, value.GetType(), JournalingSampleJsonContext.Default);
    }

    private sealed record SampleSettings(string? ConnectionString, string ContainerName, string BlobName, bool ResetBlob)
    {
        public static SampleSettings Parse(string[] args, string? aspireBlobConnectionString)
        {
            var connectionString = GetOptionValue(args, "--connection-string")
                ?? aspireBlobConnectionString
                ?? Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)
                ?? Environment.GetEnvironmentVariable(AzureWebJobsStorageEnvironmentVariable);

            var containerName = GetOptionValue(args, "--container")
                ?? Environment.GetEnvironmentVariable(ContainerEnvironmentVariable)
                ?? DefaultContainerName;

            var blobName = GetOptionValue(args, "--blob")
                ?? Environment.GetEnvironmentVariable(BlobEnvironmentVariable)
                ?? DefaultBlobName;

            var resetBlob = !args.Contains("--no-reset", StringComparer.OrdinalIgnoreCase);

            return new(connectionString, containerName, blobName, resetBlob);
        }

        private static string? GetOptionValue(string[] args, string name)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (i + 1 == args.Length)
                {
                    throw new ArgumentException($"Missing value for {name}.");
                }

                return args[i + 1];
            }

            return null;
        }
    }
}

public interface IJournaledSampleGrain : IGrainWithStringKey
{
    Task<JournaledSampleSummary> RunScenario(CancellationToken cancellationToken);
    Task<JournaledSampleSummary> GetSummary(CancellationToken cancellationToken);
    Task Deactivate(CancellationToken cancellationToken);
}

public sealed class JournaledSampleGrain(IDurableStateManager stateManager) : Grain, IJournaledSampleGrain
{
    private readonly Guid _activationId = Guid.NewGuid();

    // Declare state components during construction; Orleans recovers state before grain methods run.
    private readonly IDurableDictionary<string, InventoryItem> _inventory =
        stateManager.GetOrAddDictionary<string, InventoryItem>("inventory");
    private readonly IDurableList<JournalEvent> _events =
        stateManager.GetOrAddList<JournalEvent>("events");
    private readonly IDurableQueue<WorkItem> _workQueue =
        stateManager.GetOrAddQueue<WorkItem>("work");
    private readonly IDurableSet<string> _tags =
        stateManager.GetOrAddSet<string>("tags");
    private readonly IDurableValue<AccountBalance> _balance =
        stateManager.GetOrAddValue<AccountBalance>("balance");
    private readonly IPersistentState<ProfileState> _profile =
        stateManager.GetOrAddPersistentState<ProfileState>("profile");
    private readonly IDurableTaskCompletionSource<Receipt> _receipt =
        stateManager.GetOrAddTaskCompletionSource<Receipt>("receipt");

    public async Task<JournaledSampleSummary> RunScenario(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _inventory.Clear();
        _inventory["sku-apple"] = new InventoryItem("sku-apple", 12, 1.25m, ["fresh", "fruit"]);
        _inventory["sku-orange"] = new InventoryItem("sku-orange", 9, 1.10m, ["citrus", "fruit"]);
        _inventory.Remove("sku-orange");
        _inventory["sku-coffee"] = new InventoryItem("sku-coffee", 3, 12.99m, ["beans", "dark-roast"]);

        _events.Clear();
        _events.Add(new JournalEvent("started", DateTimeOffset.UtcNow, "scenario", ["initial write"]));
        _events.Add(new JournalEvent("inventory-loaded", DateTimeOffset.UtcNow, "inventory", ["dictionary set", "dictionary remove"]));
        _events.Insert(1, new JournalEvent("audit-inserted", DateTimeOffset.UtcNow, "audit", ["list insert"]));
        _events[0] = new JournalEvent("started-updated", DateTimeOffset.UtcNow, "scenario", ["list set"]);
        _events.RemoveAt(2);
        _events.Add(new JournalEvent("ready", DateTimeOffset.UtcNow, "scenario", ["list add"]));

        _workQueue.Clear();
        _workQueue.Enqueue(new WorkItem(Guid.NewGuid(), "ship", 10, true));
        _workQueue.Enqueue(new WorkItem(Guid.NewGuid(), "email", 2, false));
        _ = _workQueue.Dequeue();
        _workQueue.Enqueue(new WorkItem(Guid.NewGuid(), "reconcile", 7, true));

        _tags.Clear();
        _tags.Add("json");
        _tags.Add("azure-append-blob");
        _tags.Add("temporary");
        _tags.Remove("temporary");
        _tags.UnionWith(["journaling", "recovery"]);

        _balance.Value = new AccountBalance(
            "USD",
            42.75m,
            [
                new LedgerEntry("opening", 50.00m, "initial balance"),
                new LedgerEntry("coffee", -7.25m, "inventory adjustment")
            ]);

        _profile.State = new ProfileState(
            "json-codec-sample",
            2,
            DateTimeOffset.UtcNow,
            _balance.Value);

        _receipt.TrySetResult(new Receipt("receipt-001", OperationCount: 24, CompletedAt: DateTimeOffset.UtcNow));

        // One acknowledgement covers pending changes across all seven state components.
        await stateManager.WriteStateAsync(cancellationToken);
        return CreateSummary();
    }

    public Task<JournaledSampleSummary> GetSummary(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateSummary());
    }

    public Task Deactivate(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    private JournaledSampleSummary CreateSummary()
    {
        var completion = _receipt.State;
        return new JournaledSampleSummary(
            _activationId,
            _inventory.OrderBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static item => new InventoryEntry(item.Key, item.Value))
                .ToArray(),
            _events.ToArray(),
            _workQueue.ToArray(),
            _tags.Order(StringComparer.Ordinal).ToArray(),
            _balance.Value ?? throw new InvalidOperationException("Balance has not been written."),
            _profile.State,
            completion.Status,
            completion.Value);
    }
}

[GenerateSerializer]
public sealed record JournaledSampleSummary(
    [property: Id(0)] Guid ActivationId,
    [property: Id(1)] InventoryEntry[] Inventory,
    [property: Id(2)] JournalEvent[] Events,
    [property: Id(3)] WorkItem[] WorkQueue,
    [property: Id(4)] string[] Tags,
    [property: Id(5)] AccountBalance Balance,
    [property: Id(6)] ProfileState Profile,
    [property: Id(7)] DurableTaskCompletionSourceStatus CompletionStatus,
    [property: Id(8)] Receipt? Receipt);

[GenerateSerializer]
public sealed record InventoryEntry(
    [property: Id(0)] string Key,
    [property: Id(1)] InventoryItem Value);

[GenerateSerializer]
public sealed record InventoryItem(
    [property: Id(0)] string Sku,
    [property: Id(1)] int Quantity,
    [property: Id(2)] decimal UnitPrice,
    [property: Id(3)] string[] Attributes);

[GenerateSerializer]
public sealed record JournalEvent(
    [property: Id(0)] string EventId,
    [property: Id(1)] DateTimeOffset Timestamp,
    [property: Id(2)] string Kind,
    [property: Id(3)] string[] Notes);

[GenerateSerializer]
public sealed record WorkItem(
    [property: Id(0)] Guid WorkId,
    [property: Id(1)] string Kind,
    [property: Id(2)] int Priority,
    [property: Id(3)] bool Urgent);

[GenerateSerializer]
public sealed record AccountBalance(
    [property: Id(0)] string Currency,
    [property: Id(1)] decimal Amount,
    [property: Id(2)] LedgerEntry[] Ledger);

[GenerateSerializer]
public sealed record LedgerEntry(
    [property: Id(0)] string Id,
    [property: Id(1)] decimal Amount,
    [property: Id(2)] string Reason);

[GenerateSerializer]
public sealed record ProfileState(
    [property: Id(0)] string Owner,
    [property: Id(1)] int Revision,
    [property: Id(2)] DateTimeOffset UpdatedAt,
    [property: Id(3)] AccountBalance LastBalance);

[GenerateSerializer]
public sealed record Receipt(
    [property: Id(0)] string ReceiptId,
    [property: Id(1)] int OperationCount,
    [property: Id(2)] DateTimeOffset CompletedAt);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(JournaledSampleSummary))]
[JsonSerializable(typeof(InventoryEntry))]
[JsonSerializable(typeof(InventoryItem))]
[JsonSerializable(typeof(JournalEvent))]
[JsonSerializable(typeof(WorkItem))]
[JsonSerializable(typeof(AccountBalance))]
[JsonSerializable(typeof(LedgerEntry))]
[JsonSerializable(typeof(ProfileState))]
[JsonSerializable(typeof(Receipt))]
[JsonSerializable(typeof(DurableTaskCompletionSourceStatus))]
internal sealed partial class JournalingSampleJsonContext : JsonSerializerContext;
