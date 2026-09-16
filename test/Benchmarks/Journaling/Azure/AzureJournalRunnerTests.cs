using System.Diagnostics.Metrics;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Data.Tables.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DurableJobsJournaling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Buffers;
using TestExtensions;
using Xunit;

namespace Benchmarks.Journaling.Azure;

[TestCategory("BVT")]
public class AzureJournalRunnerTests
{
    [Theory]
    [InlineData("--concurrency", "0")]
    [InlineData("--operations", "10001")]
    [InlineData("--payload-bytes", "2097153")]
    [InlineData("--max-work", "1")]
    [InlineData("--max-bytes", "1")]
    [InlineData("--timeout-seconds", "0")]
    [InlineData("--backend", "PremiumBlob")]
    [InlineData("--backend", "2")]
    [InlineData("--unknown", "value")]
    public void ParserRejectsUnsafeWork(string name, string value)
        => Assert.Throws<ArgumentException>(() => AzureJournalOptions.Parse([name, value]));

    [Fact]
    public void ParserEnforcesCommonAppendLimitAndTotalWork()
    {
        var valid = AzureJournalOptions.Parse(["--payload-bytes", "1048576", "--batch-size", "2", "--history-batches", "0"]);
        Assert.Equal(2 * 1024 * 1024, valid.AppendBytes);
        Assert.Throws<ArgumentException>(() => AzureJournalOptions.Parse(["--payload-bytes", "1048577", "--batch-size", "2"]));
        Assert.Throws<ArgumentException>(() => AzureJournalOptions.Parse(["--operations", "1", "--operations", "2"]));
        Assert.Throws<ArgumentException>(() => AzureJournalOptions.Parse(["--operations"]));
        Assert.Throws<ArgumentException>(() => AzureJournalOptions.Parse(["--workload", "CatalogUnbounded", "--future-journals", "100000"]));
    }

    [Fact]
    public void AccountVerificationDistinguishesEmulatorStandardAndPremium()
    {
        AzureJournalScenario.VerifyAccount(AzureJournalBackend.StandardBlob, AccountKind.StorageV2, SkuName.StandardLrs);
        AzureJournalScenario.VerifyAccount(AzureJournalBackend.PremiumBlob, AccountKind.BlockBlobStorage, SkuName.PremiumLrs);
        Assert.Throws<InvalidOperationException>(() => AzureJournalScenario.VerifyAccount(AzureJournalBackend.AzuriteBlob, AccountKind.StorageV2, SkuName.StandardLrs));
        Assert.Throws<InvalidOperationException>(() => AzureJournalScenario.VerifyAccount(AzureJournalBackend.AzuriteTable, AccountKind.StorageV2, SkuName.StandardLrs));
        Assert.Throws<InvalidOperationException>(() => AzureJournalScenario.VerifyAccount(AzureJournalBackend.StandardBlob, (AccountKind)int.MaxValue, SkuName.StandardLrs));
        Assert.Throws<InvalidOperationException>(() => AzureJournalScenario.VerifyAccount(AzureJournalBackend.PremiumBlob, AccountKind.BlockBlobStorage, (SkuName)int.MaxValue));
        Assert.Throws<InvalidOperationException>(() => AzureJournalScenario.VerifyAccount(AzureJournalBackend.StandardBlob, AccountKind.StorageV2, (SkuName)int.MaxValue));
        Assert.True(AzureJournalOptions.Parse(["--backend", "Table", "--allow-azure", "true"]).AllowAzure);
    }

    [Theory]
    [InlineData(AzureJournalBackend.StandardBlob, AccountKind.Storage, SkuName.StandardLrs)]
    [InlineData(AzureJournalBackend.StandardBlob, AccountKind.BlobStorage, SkuName.StandardLrs)]
    [InlineData(AzureJournalBackend.StandardBlob, AccountKind.StorageV2, SkuName.StandardLrs)]
    [InlineData(AzureJournalBackend.StandardBlob, AccountKind.StorageV2, SkuName.StandardGrs)]
    [InlineData(AzureJournalBackend.StandardBlob, AccountKind.StorageV2, SkuName.StandardRagrs)]
    [InlineData(AzureJournalBackend.StandardBlob, AccountKind.StorageV2, SkuName.StandardZrs)]
    [InlineData(AzureJournalBackend.PremiumBlob, AccountKind.BlockBlobStorage, SkuName.PremiumLrs)]
    public void AccountVerificationAcceptsSupportedSdkSkus(AzureJournalBackend backend, AccountKind kind, SkuName sku)
        => AzureJournalScenario.VerifyAccount(backend, kind, sku);

    [Theory]
    [InlineData(AzureJournalBackend.PremiumBlob, AccountKind.StorageV2, SkuName.PremiumLrs)]
    [InlineData(AzureJournalBackend.PremiumBlob, AccountKind.BlockBlobStorage, SkuName.StandardLrs)]
    [InlineData(AzureJournalBackend.StandardBlob, AccountKind.StorageV2, SkuName.PremiumLrs)]
    [InlineData(AzureJournalBackend.StandardBlob, AccountKind.FileStorage, SkuName.StandardLrs)]
    [InlineData(AzureJournalBackend.StandardBlob, AccountKind.BlockBlobStorage, SkuName.StandardLrs)]
    public void AccountVerificationRejectsMismatchedSdkKindAndTier(AzureJournalBackend backend, AccountKind kind, SkuName sku)
        => Assert.Throws<InvalidOperationException>(() => AzureJournalScenario.VerifyAccount(backend, kind, sku));

    [Theory]
    [InlineData("https://account.blob.core.windows.net/?sig=secret", false)]
    [InlineData("https://user:secret@account.blob.core.windows.net/", false)]
    [InlineData("http://account.blob.core.windows.net/", false)]
    [InlineData("https://account.blob.core.windows.net/", true)]
    [InlineData("http://127.0.0.1:10000/devstoreaccount1", false)]
    public void EndpointsEnforceAuthenticationAndEmulatorIsolation(string endpoint, bool emulator)
        => Assert.Throws<ArgumentException>(() => AzureJournalScenario.ValidateEndpoint(new Uri(endpoint), emulator));

    [Fact]
    public void PercentilesUseNearestRankAndExcludeFailures()
    {
        var report = new AzureJournalReport(new() { Operations = 100, Concurrency = 1 }) { ElapsedSeconds = 2 };
        report.Operations.AddRange(Enumerable.Range(1, 99).Select(value => new OperationResult(value, "completed", value, 10, 1)));
        report.Operations.Add(new(100, "failed", 10000, 0, 0));
        Assert.Equal(new LatencySummary(99, 50, 50, 95, 99), report.SuccessfulLatency);
        Assert.Equal(49.5, report.CompletedOperationsPerSecond);
        Assert.Equal(495, report.PayloadBytesPerSecond);
        Assert.Equal(1, report.Failed);
        Assert.Equal(0, report.NotStarted);
        Assert.Null(LatencySummary.Create([]).P99Ms);
        Assert.False(report.Success);
    }

    [Fact]
    public void MetricsUseExactMeterAndMeasurementWindow()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var instruments = new OrleansInstruments(services.GetRequiredService<IMeterFactory>());
        using var foreign = new Meter("Microsoft.Orleans");
        var foreignCount = foreign.CreateCounter<long>("orleans-journaling-provider-catalog-pages");
        using var collector = new ProviderMetrics(instruments.Meter);
        var telemetry = new JournalStorageTelemetry(instruments);
        telemetry.OnCatalogPage("orders-primary", 100);
        collector.Start();
        foreignCount.Add(1000);
        telemetry.OnCatalogPage("orders-primary", 0);
        telemetry.OnCatalogPage("orders-primary", 3);
        telemetry.OnCatalogEntry("orders-primary");
        telemetry.OnCatalogEntry("orders-primary");
        telemetry.OnRetry("orders-primary", "metadata_conflict");
        telemetry.OnRetry("orders-primary", "metadata_conflict");
        telemetry.OnRetry("orders-secondary", "metadata_conflict");
        var metrics = collector.Stop();
        telemetry.OnCatalogPage("orders-primary", 100);
        Assert.Equal(new ProviderMetric[]
        {
            new("orleans-journaling-provider-catalog-entries", "count", "orders-primary", "", 2, 2),
            new("orleans-journaling-provider-catalog-items", "count", "orders-primary", "", 1, 3),
            new("orleans-journaling-provider-catalog-pages", "count", "orders-primary", "", 2, 2),
            new("orleans-journaling-provider-retries", "count", "orders-primary", "metadata_conflict", 2, 2),
            new("orleans-journaling-provider-retries", "count", "orders-secondary", "metadata_conflict", 1, 1)
        }, metrics);
        collector.Start();
        Assert.Empty(collector.Stop());
    }

    [Fact]
    public void MetricsEnableCountersOnlyDuringExplicitCollectionWindows()
    {
        using var meter = new Meter("Microsoft.Orleans");
        var pages = meter.CreateCounter<long>("orleans-journaling-provider-catalog-pages");
        using var collector = new ProviderMetrics(meter);
        var entries = meter.CreateCounter<long>("orleans-journaling-provider-catalog-entries");
        Assert.False(pages.Enabled);
        Assert.False(entries.Enabled);
        pages.Add(100);
        collector.Start();
        Assert.True(pages.Enabled);
        Assert.True(entries.Enabled);
        Assert.Throws<InvalidOperationException>(collector.Start);
        var retries = meter.CreateCounter<long>("orleans-journaling-provider-retries");
        Assert.True(retries.Enabled);
        pages.Add(1, new KeyValuePair<string, object?>("provider", "orders"));
        entries.Add(2, new KeyValuePair<string, object?>("provider", "orders"));
        Assert.Equal(new ProviderMetric[]
        {
            new("orleans-journaling-provider-catalog-entries", "count", "orders", "", 1, 2),
            new("orleans-journaling-provider-catalog-pages", "count", "orders", "", 1, 1)
        }, collector.Stop());
        Assert.False(pages.Enabled);
        Assert.False(entries.Enabled);
        Assert.False(retries.Enabled);
        entries.Add(100);
        collector.Start();
        Assert.True(pages.Enabled);
        Assert.True(entries.Enabled);
        Assert.True(retries.Enabled);
        entries.Add(3, new KeyValuePair<string, object?>("provider", "orders"));
        Assert.Equal(new ProviderMetric("orleans-journaling-provider-catalog-entries", "count", "orders", "", 1, 3),
            Assert.Single(collector.Stop()));
        collector.Start();
        Assert.True(pages.Enabled);
        collector.Dispose();
        Assert.False(pages.Enabled);
        Assert.False(entries.Enabled);
        Assert.False(retries.Enabled);
    }

    [Fact]
    public void MetricsCollectOnlyCatalogCountersAndPreserveIntegerPrecision()
    {
        using var meter = new Meter("Microsoft.Orleans");
        using var collector = new ProviderMetrics(meter);
        var items = meter.CreateCounter<long>("orleans-journaling-provider-catalog-items");
        var oldCalls = meter.CreateCounter<long>("orleans-journaling-provider-api-calls");
        var oldOperations = meter.CreateCounter<long>("orleans-journaling-provider-operations");
        var duration = meter.CreateHistogram<double>("orleans-journaling-provider-operation-duration", "ms");
        var wrongType = meter.CreateHistogram<long>("orleans-journaling-provider-catalog-pages");
        var futureMetric = meter.CreateCounter<long>("orleans-journaling-provider-unrelated");
        collector.Start();
        const long Count = 9_007_199_254_740_993;
        items.Add(Count, new KeyValuePair<string, object?>("provider", "orders"));
        items.Add(2, new KeyValuePair<string, object?>("provider", "orders"));
        oldCalls.Add(100);
        oldOperations.Add(100);
        duration.Record(5);
        wrongType.Record(10);
        futureMetric.Add(100);
        Assert.Equal(new ProviderMetric("orleans-journaling-provider-catalog-items", "count", "orders", "", 2, Count + 2),
            Assert.Single(collector.Stop()));
    }

    [Fact]
    public async Task MetricsCaptureActualVolatileCatalogEntries()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var instruments = new OrleansInstruments(services.GetRequiredService<IMeterFactory>());
        using var collector = new ProviderMetrics(instruments.Meter);
        var provider = new VolatileJournalStorageProvider(Options.Create(new JournaledStateManagerOptions()), instruments);
        var token = TestContext.Current.CancellationToken;
        Assert.True(await provider.CreateStorage(new("due/a")).CreateIfNotExistsAsync(cancellationToken: token));
        Assert.True(await provider.CreateStorage(new("due/b")).CreateIfNotExistsAsync(cancellationToken: token));
        Assert.True(await provider.CreateStorage(new("future/c")).CreateIfNotExistsAsync(cancellationToken: token));
        collector.Start();
        var ids = new List<JournalId>();
        await foreach (var entry in provider.ListAsync(new() { Prefix = new("due/") }, token))
        {
            ids.Add(entry.Id);
        }

        Assert.Equal(new JournalId[] { new("due/a"), new("due/b") }, ids);
        Assert.Equal(new ProviderMetric("orleans-journaling-provider-catalog-entries", "count", "volatile", "", 2, 2),
            Assert.Single(collector.Stop()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetricsCaptureActualAzureCatalogPagesCandidatesAndEntries(bool table)
    {
        var builder = new MetricsSiloBuilder();
        builder.Services.AddLogging().AddMetrics().AddSingleton<OrleansInstruments>();
        if (table)
        {
            builder.AddAzureTableJournalStorage(options => options.TableServiceClient = new CatalogTableService());
        }
        else
        {
            builder.AddAzureBlobJournalStorage(options => options.BlobServiceClient = new CatalogBlobService());
        }

        await using var services = builder.Services.BuildServiceProvider();
        var meter = services.GetRequiredService<OrleansInstruments>().Meter;
        var counters = new List<Instrument>();
        using var observer = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (ReferenceEquals(instrument.Meter, meter)
                    && instrument.Name.StartsWith("orleans-journaling-provider-", StringComparison.Ordinal))
                {
                    counters.Add(instrument);
                }
            }
        };
        observer.Start();
        using var collector = new ProviderMetrics(meter);
        var catalog = services.GetRequiredService<IJournalStorageCatalog>();
        Assert.Equal(4, counters.Count);
        Assert.All(counters, counter => Assert.False(counter.Enabled));
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        foreach (var participant in services.GetServices<ILifecycleParticipant<ISiloLifecycle>>())
        {
            participant.Participate(lifecycle);
        }

        var token = TestContext.Current.CancellationToken;
        await lifecycle.OnStart(token);
        try
        {
            var options = new ListOptions { Prefix = new("catalog/"), MaxId = new("catalog/b") };
            Assert.Equal(new JournalId[] { new("catalog/a"), new("catalog/b") }, await ReadIdsAsync());
            Assert.All(counters, counter => Assert.False(counter.Enabled));
            collector.Start();
            Assert.All(counters, counter => Assert.True(counter.Enabled));
            Assert.Equal(new JournalId[] { new("catalog/a"), new("catalog/b") }, await ReadIdsAsync());
            var provider = table ? "azure_table" : "azure_blob";
            Assert.Equal(new ProviderMetric[]
            {
                new("orleans-journaling-provider-catalog-entries", "count", provider, "", 2, 2),
                new("orleans-journaling-provider-catalog-items", "count", provider, "", 1, 3),
                new("orleans-journaling-provider-catalog-pages", "count", provider, "", 2, 2)
            }, collector.Stop());
            Assert.All(counters, counter => Assert.False(counter.Enabled));
            Assert.Equal(new JournalId[] { new("catalog/a"), new("catalog/b") }, await ReadIdsAsync());
            Assert.All(counters, counter => Assert.False(counter.Enabled));

            async Task<List<JournalId>> ReadIdsAsync()
            {
                var ids = new List<JournalId>();
                await foreach (var entry in catalog.ListAsync(options, token))
                {
                    ids.Add(entry.Id);
                }

                return ids;
            }
        }
        finally
        {
            await lifecycle.OnStop(token);
        }
    }

    [Fact]
    public async Task RunnerJoinsBoundedWorkersBeforeCleanupAndPreservesCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var cleanupCalled = false;
        var task = AzureJournalRunner.RunAsync(new() { Operations = 6, Concurrency = 2 }, cancellation.Token, (_, report) =>
            new FakeScenario(report)
            {
                Execute = async (_, token) =>
                {
                    if (Interlocked.Increment(ref active) == 2)
                    {
                        entered.SetResult();
                    }

                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref active);
                    }
                },
                CleanupAction = token =>
                {
                    Assert.False(token.IsCancellationRequested);
                    Assert.Equal(0, active);
                    cleanupCalled = true;
                    return Task.CompletedTask;
                }
            });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(cleanupCalled);
        Assert.Equal(0, result.Completed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(2, result.Cancelled);
        Assert.Equal(4, result.NotStarted);
        Assert.Equal("deleted", result.Cleanup);
        Assert.False(result.Success);
        Assert.All(result.Failures, failure => Assert.Equal("measurement", failure.Phase));
    }

    [Fact]
    public async Task RunnerReportsPartialFailureAndIndependentCleanupFailure()
    {
        var result = await AzureJournalRunner.RunAsync(new() { Operations = 4, Concurrency = 1 }, TestContext.Current.CancellationToken, createScenario: (_, report) =>
            new FakeScenario(report)
            {
                Execute = (index, _) => index == 1 ? throw new InvalidDataException("sensitive message") : Task.CompletedTask,
                CleanupAction = _ => throw new TimeoutException("secret endpoint")
            });
        Assert.Equal(1, result.Completed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, result.NotStarted);
        Assert.Equal("measurement", result.Phase);
        Assert.Equal("failed", result.Cleanup);
        Assert.Collection(result.Failures,
            failure => Assert.Equal(new BenchmarkFailure("measurement", "InvalidDataException", null, 1), failure),
            failure => Assert.Equal(new BenchmarkFailure("cleanup", "TimeoutException", null), failure));
        Assert.False(result.Success);
    }

    [Fact]
    public async Task SetupFailureStillCleansOwnedResourceAndMeasuresNothing()
    {
        var result = await AzureJournalRunner.RunAsync(new(), TestContext.Current.CancellationToken, createScenario: (_, report) =>
            new FakeScenario(report) { Prepare = _ => throw new InvalidDataException() });
        Assert.Equal("setup", Assert.Single(result.Failures).Phase);
        Assert.Empty(result.Operations);
        Assert.Empty(result.ProviderMetrics);
        Assert.Equal(0, result.ElapsedSeconds);
        Assert.Equal("deleted", result.Cleanup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialProviderInitializationStopsAttemptedStagesBeforeDisposal(bool cancel)
    {
        var events = new List<string>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var builder = new MetricsSiloBuilder();
        builder.Services.AddLogging().AddMetrics().AddSingleton<OrleansInstruments>();
        builder.AddAzureBlobJournalStorage(options => options.BlobServiceClient = new CatalogBlobService());
        var startupFailure = new InvalidOperationException("startup failed");
        builder.Services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(_ => new FailingLifecycleParticipant(
            events, () =>
            {
                if (cancel)
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }

                throw startupFailure;
            }));
        var options = new AzureJournalOptions();
        var report = new AzureJournalReport(options);
        var scenario = new AzureJournalScenario(options, report);
        try
        {
            var error = await Record.ExceptionAsync(() => scenario.InitializeProviderAsync(builder.Services, cancellation.Token));
            if (cancel)
            {
                Assert.IsAssignableFrom<OperationCanceledException>(error);
            }
            else
            {
                Assert.Same(startupFailure, error);
            }

            report.Failures.Add(BenchmarkFailure.From(report.Phase, error!));
            Assert.Equal(new[] { "start:early", "start:failed" }, events);
            await scenario.CleanupAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "start:early", "start:failed", "stop:failed", "stop:early", "dispose" }, events);
            Assert.Equal("initialize", Assert.Single(report.Failures).Phase);
            Assert.False(report.Success);
            await scenario.CleanupAsync(TestContext.Current.CancellationToken);
            Assert.Equal(5, events.Count);
        }
        finally
        {
            await scenario.CleanupAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task VerificationFailureRetainsCompletedOperationsAndFailsTheRun()
    {
        var result = await AzureJournalRunner.RunAsync(new() { Operations = 2 }, TestContext.Current.CancellationToken, createScenario: (_, report) =>
            new FakeScenario(report) { Verify = _ => throw new BenchmarkValidationException(BenchmarkValidationError.RecoveryCompletionOrChecksum) });
        Assert.Equal(2, result.Completed);
        Assert.Equal(0, result.NotStarted);
        Assert.Equal("verification", result.Phase);
        Assert.Equal("verification", Assert.Single(result.Failures).Phase);
        Assert.Equal(BenchmarkValidationError.RecoveryCompletionOrChecksum, result.Failures[0].ValidationError);
        Assert.Equal("deleted", result.Cleanup);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task ResourceCollisionAndUnconfirmedCreationNeverDelete()
    {
        foreach (var exception in new Exception[] { new RequestFailedException(409, "collision"), new OperationCanceledException() })
        {
            var report = new AzureJournalReport(new());
            var deleted = false;
            var resource = new OwnedBenchmarkResource(report, _ => Task.FromException(exception), _ =>
            {
                deleted = true;
                return Task.CompletedTask;
            });
            Assert.Same(exception, await Record.ExceptionAsync(() => resource.CreateAsync(CancellationToken.None)));
            await resource.CleanupAsync(CancellationToken.None);
            Assert.False(deleted);
            Assert.Equal("creation-unconfirmed", report.Ownership);
            Assert.Equal("manual-check-required", report.Cleanup);
        }
    }

    [Fact]
    public async Task LostCreateResponseFollowedByRetryConflictRequiresManualInspection()
    {
        var report = new AzureJournalReport(new()) { Resource = "journalbenchownedbythisattempt" };
        var existsOnServer = false;
        var deletes = 0;
        var resource = new OwnedBenchmarkResource(report, _ =>
        {
            existsOnServer = true;
            // The SDK observes a conflict on retry after losing the successful create response.
            return Task.FromException(new RequestFailedException(409, "ResourceAlreadyExists"));
        }, _ =>
        {
            deletes++;
            return Task.CompletedTask;
        });
        var failure = await Assert.ThrowsAsync<RequestFailedException>(() => resource.CreateAsync(TestContext.Current.CancellationToken));
        report.Failures.Add(BenchmarkFailure.From("setup", failure));
        await resource.CleanupAsync(TestContext.Current.CancellationToken);
        Assert.True(existsOnServer);
        Assert.Equal(0, deletes);
        Assert.Equal("creation-unconfirmed", report.Ownership);
        Assert.Equal("manual-check-required", report.Cleanup);
        Assert.Equal("journalbenchownedbythisattempt", report.Resource);
        Assert.Equal(409, Assert.Single(report.Failures).HttpStatus);
        Assert.False(report.Success);
    }

    [Fact]
    public async Task AcknowledgedResourceIsDeletedExactlyOnce()
    {
        var report = new AzureJournalReport(new());
        var deletes = 0;
        var resource = new OwnedBenchmarkResource(report, _ => Task.CompletedTask, _ =>
        {
            deletes++;
            return Task.CompletedTask;
        });
        await resource.CreateAsync(CancellationToken.None);
        Assert.Equal("owned", report.Ownership);
        await resource.CleanupAsync(CancellationToken.None);
        await resource.CleanupAsync(CancellationToken.None);
        Assert.Equal(1, deletes);
        Assert.Equal("deleted", report.Cleanup);
    }

    [Fact]
    public async Task ReportsExportConfigurationUnitsFailuresAndProviderMetricsWithoutSecrets()
    {
        var prefix = Path.Combine(Path.GetTempPath(), "journal-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            const long Items = 9_007_199_254_740_993;
            var report = new AzureJournalReport(new() { Output = "secret-path", Operations = 1, Concurrency = 1 })
            {
                ProviderMetrics =
                [
                    new("orleans-journaling-provider-catalog-items", "count", "orders", "", 1, Items),
                    new("orleans-journaling-provider-retries", "count", "orders", "metadata_conflict", 1, 1)
                ]
            };
            report.Failures.Add(BenchmarkFailure.From("setup", new RequestFailedException(403, "sig=secret")));
            await report.ExportAsync(prefix);
            var json = await File.ReadAllTextAsync(prefix + ".json", TestContext.Current.CancellationToken);
            var csv = await File.ReadAllTextAsync(prefix + ".csv", TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(2, document.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal("counts of catalog pages, candidate items, delivered entries, and explicit provider retries",
                document.RootElement.GetProperty("TelemetryUnit").GetString());
            var metric = document.RootElement.GetProperty("ProviderMetrics")[0];
            Assert.Equal(new[] { "Instrument", "Unit", "Provider", "Reason", "Observations", "Sum" },
                metric.EnumerateObject().Select(property => property.Name));
            Assert.Equal(Items, metric.GetProperty("Sum").GetInt64());
            Assert.Equal("metadata_conflict", document.RootElement.GetProperty("ProviderMetrics")[1].GetProperty("Reason").GetString());
            Assert.Equal("AzuriteBlob", document.RootElement.GetProperty("Configuration").GetProperty("Backend").GetString());
            Assert.Equal(403, document.RootElement.GetProperty("Failures")[0].GetProperty("HttpStatus").GetInt32());
            Assert.Contains("provider_metrics_json", csv);
            Assert.Contains("payload_bytes_per_second", csv);
            Assert.Contains(JsonSerializer.Serialize(report.ProviderMetrics, AzureJournalReport.JsonOptions).Replace("\"", "\"\"", StringComparison.Ordinal), csv);
            Assert.Contains("build_json", csv);
            Assert.Equal(report.Build, document.RootElement.GetProperty("Build").Deserialize<BenchmarkBuildInfo>());
            Assert.Contains(JsonSerializer.Serialize(report.Build, AzureJournalReport.JsonOptions).Replace("\"", "\"\"", StringComparison.Ordinal), csv);
            Assert.Equal("IJournalStorage / IJournalStorageCatalog", document.RootElement.GetProperty("Source").GetString());
            Assert.DoesNotContain("secret", json);
            Assert.DoesNotContain("secret", csv);
            await Assert.ThrowsAsync<IOException>(() => report.ExportAsync(prefix));
        }
        finally
        {
            File.Delete(prefix + ".json");
            File.Delete(prefix + ".csv");
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("10.0.0. Commit Hash: <developer build>", null)]
    [InlineData("10.0.0+short", null)]
    [InlineData("10.0.0+https://example.invalid/?sig=sensitive", null)]
    [InlineData("10.0.0+012345678901234567890123456789012345678G", null)]
    [InlineData("10.0.0+0123456789012345678901234567890123456789-dirty", null)]
    [InlineData("10.0.0. Commit Hash: <developer build>+ABCDEF0123456789ABCDEF0123456789ABCDEF01", "abcdef0123456789abcdef0123456789abcdef01")]
    [InlineData("10.0.0+0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void BuildProvenanceExportsOnlySanitizedSourceRevision(string? version, string? expected)
        => Assert.Equal(expected, BenchmarkAssemblyBuild.ParseSourceRevision(version));

    [Fact]
    public void BuildProvenanceUsesLoadedAssemblyMetadata()
    {
        const string Revision = "0123456789012345678901234567890123456789";
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("BenchmarkProvenanceFixture"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Fixture");
        assembly.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, ["10.0.0+" + Revision]));
        Assert.Equal(new BenchmarkAssemblyBuild("BenchmarkProvenanceFixture", Revision, module.ModuleVersionId),
            BenchmarkAssemblyBuild.FromAssembly(assembly));
    }

    [Fact]
    public void BuildProvenanceIdentifiesBenchmarkAndProviderModulesInBdnLog()
    {
        var build = new AzureJournalReport(new()).Build;
        Assert.Equal("Benchmarks", build.Benchmark.Name);
        Assert.Equal(typeof(AzureJournalReport).Module.ModuleVersionId, build.Benchmark.ModuleVersionId);
        Assert.Equal("Orleans.Journaling", build.Journaling.Name);
        Assert.Equal(typeof(IJournalStorage).Module.ModuleVersionId, build.Journaling.ModuleVersionId);
        Assert.Equal("Orleans.Journaling.AzureStorage", build.AzureStorage.Name);
        Assert.Equal(typeof(AzureBlobJournalStorageOptions).Module.ModuleVersionId, build.AzureStorage.ModuleVersionId);
        Assert.NotEqual(Guid.Empty, build.Benchmark.ModuleVersionId);
        Assert.NotEqual(Guid.Empty, build.Journaling.ModuleVersionId);
        Assert.NotEqual(Guid.Empty, build.AzureStorage.ModuleVersionId);
        using var output = new StringWriter();
        BenchmarkBuildInfo.WriteTo(output);
        var line = Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        const string Prefix = "Azure benchmark build: ";
        Assert.StartsWith(Prefix, line);
        Assert.Equal(build, JsonSerializer.Deserialize<BenchmarkBuildInfo>(line[Prefix.Length..]));
        Assert.NotNull(typeof(AzureJournalBenchmarks).GetMethod(nameof(AzureJournalBenchmarks.ReportBuild))!
            .GetCustomAttribute<BenchmarkDotNet.Attributes.GlobalSetupAttribute>());
    }

    [Fact]
    public void RecoveryConsumesEveryChunkAndRequiresCompletionCountAndChecksum()
    {
        byte[] checkpoint = [1, 2, 3];
        byte[] append = [4, 5];
        var metadata = new JournalMetadata(AzureJournalScenario.FormatKey, "etag", AzureJournalScenario.CallerMetadata);
        using var consumer = new AzureJournalScenario.PayloadConsumer(checkpoint, append, 2);
        using var buffer = new ArcBufferWriter();
        buffer.Write(new byte[] { 1, 2, 3, 4 });
        consumer.Read(new JournalBufferReader(buffer.Reader, false), metadata);
        Assert.Equal(0, buffer.Reader.Length);
        Assert.Throws<BenchmarkValidationException>(consumer.AssertComplete);
        buffer.Write(new byte[] { 5, 4, 5 });
        consumer.Read(new JournalBufferReader(buffer.Reader, true), metadata);
        Assert.Equal(0, buffer.Reader.Length);
        consumer.AssertComplete();
        Assert.Throws<BenchmarkValidationException>(() => consumer.Read(new JournalBufferReader(buffer.Reader, true), metadata));

        using var corrupt = new AzureJournalScenario.PayloadConsumer(checkpoint, append, 0);
        buffer.Write(new byte[] { 1, 2, 0 });
        Assert.Throws<BenchmarkValidationException>(() => corrupt.Read(new JournalBufferReader(buffer.Reader, true), metadata));
        using var shortRead = new AzureJournalScenario.PayloadConsumer(checkpoint, append, 1);
        using var shortBuffer = new ArcBufferWriter();
        shortBuffer.Write(checkpoint);
        shortRead.Read(new JournalBufferReader(shortBuffer.Reader, true), metadata);
        Assert.Throws<BenchmarkValidationException>(shortRead.AssertComplete);
    }

    [Fact]
    public void CatalogBoundsSelectDueIdentitiesAndPreserveMetadataProjection()
    {
        var options = new AzureJournalOptions { Workload = AzureJournalWorkload.CatalogBounded, DueJournals = 3, FutureJournals = 9, IncludeMetadata = true };
        var scenario = new AzureJournalScenario(options, new(options));
        var listing = scenario.CatalogOptions();
        Assert.Equal(new JournalId("catalog/"), listing.Prefix);
        Assert.Equal(AzureJournalScenario.CatalogId(0, false), listing.MinId);
        Assert.Equal(AzureJournalScenario.CatalogId(2, false), listing.MaxId);
        Assert.True(listing.IncludeMetadata);
        Assert.Equal(3, options.ItemsPerOperation);
        var unbounded = options with { Workload = AzureJournalWorkload.CatalogUnbounded };
        Assert.True(new AzureJournalScenario(unbounded, new(unbounded)).CatalogOptions().MaxId.IsDefault);
        Assert.Equal(12, unbounded.ItemsPerOperation);
        Assert.Throws<BenchmarkValidationException>(() => AzureJournalScenario.ValidateMetadata(new JournalMetadata(AzureJournalScenario.FormatKey, "etag"), true));
        Assert.Throws<BenchmarkValidationException>(() => AzureJournalScenario.ValidateMetadata(new JournalMetadata("json", "etag", AzureJournalScenario.CallerMetadata), true));
    }

    [Fact]
    public async Task CatalogConsumesAndValidatesExactMembershipAndMetadata()
    {
        var first = AzureJournalScenario.CatalogId(0, false);
        var second = AzureJournalScenario.CatalogId(1, false);
        var future = AzureJournalScenario.CatalogId(0, true);
        HashSet<JournalId> expected = [first, second];
        var metadata = new JournalMetadata(AzureJournalScenario.FormatKey, "etag", AzureJournalScenario.CallerMetadata);
        var token = TestContext.Current.CancellationToken;
        await AzureJournalScenario.ValidateCatalogAsync(Entries([]), new HashSet<JournalId>(), false, token);
        await AzureJournalScenario.ValidateCatalogAsync(Entries([new(second, metadata), new(first, metadata)]), expected, true, token);
        var missing = await Assert.ThrowsAsync<BenchmarkValidationException>(() =>
            AzureJournalScenario.ValidateCatalogAsync(Entries([new(first, metadata)]), expected, true, token));
        Assert.Equal(BenchmarkValidationError.CatalogMissingIdentity, missing.Code);
        foreach (var extra in new[] { first, future })
        {
            var invalid = await Assert.ThrowsAsync<BenchmarkValidationException>(() =>
                AzureJournalScenario.ValidateCatalogAsync(Entries([new(first, metadata), new(extra, metadata)]), expected, true, token));
            Assert.Equal(BenchmarkValidationError.CatalogUnexpectedIdentity, invalid.Code);
        }

        await AzureJournalScenario.ValidateCatalogAsync(Entries([new(first, null), new(second, null)]), expected, false, token);
        var unexpectedMetadata = await Assert.ThrowsAsync<BenchmarkValidationException>(() =>
            AzureJournalScenario.ValidateCatalogAsync(Entries([new(first, metadata), new(second, metadata)]), expected, false, token));
        Assert.Equal(BenchmarkValidationError.CatalogUnrequestedMetadata, unexpectedMetadata.Code);
        var missingMetadata = await Assert.ThrowsAsync<BenchmarkValidationException>(() =>
            AzureJournalScenario.ValidateCatalogAsync(Entries([new(first, null), new(second, metadata)]), expected, true, token));
        Assert.Equal(BenchmarkValidationError.JournalFormatOrETag, missingMetadata.Code);

        static async IAsyncEnumerable<JournalCatalogEntry> Entries(JournalCatalogEntry[] values)
        {
            await Task.Yield();
            foreach (var value in values)
            {
                yield return value;
            }
        }
    }

    [Theory]
    [InlineData("Azurite", "Azurite", true, false)]
    [InlineData("AzuriteTable", "AzuriteTable", true, true)]
    [InlineData("StandardBlob", "StandardBlob", false, false)]
    [InlineData("PremiumBlob", "PremiumBlob", false, false)]
    [InlineData("Azure", "PremiumBlob", false, false)]
    [InlineData("Table", "Table", false, true)]
    public void PlaygroundRoutesBackendAndKeepsAzureAlias(string name, string expected, bool emulator, bool table)
    {
        var backend = StorageBackendConfiguration.Parse(name);
        Assert.Equal(expected, backend.ToString());
        Assert.Equal(emulator, backend.IsEmulator());
        Assert.Equal(table, backend.UsesTableJournal());
    }

    private sealed class MetricsSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class FailingLifecycleParticipant(List<string> events, Action fail) : ILifecycleParticipant<ISiloLifecycle>, IDisposable
    {
        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe("early", ServiceLifecycleStage.RuntimeInitialize + 1,
                onStart: _ => { events.Add("start:early"); return Task.CompletedTask; },
                onStop: token => { token.ThrowIfCancellationRequested(); events.Add("stop:early"); return Task.CompletedTask; });
            lifecycle.Subscribe("failed", ServiceLifecycleStage.RuntimeInitialize + 2,
                onStart: _ => { events.Add("start:failed"); fail(); return Task.CompletedTask; },
                onStop: token => { token.ThrowIfCancellationRequested(); events.Add("stop:failed"); return Task.CompletedTask; });
            lifecycle.Subscribe("unreached", ServiceLifecycleStage.RuntimeInitialize + 3,
                onStart: _ => { events.Add("start:unreached"); return Task.CompletedTask; },
                onStop: _ => { events.Add("stop:unreached"); return Task.CompletedTask; });
        }

        public void Dispose() => events.Add("dispose");
    }

    private sealed class CatalogBlobService : BlobServiceClient
    {
        private readonly CatalogContainer _container = new();
        public override BlobContainerClient GetBlobContainerClient(string blobContainerName) => _container;
    }

    private sealed class CatalogContainer : BlobContainerClient
    {
        public override Task<Response<BlobContainerInfo>> CreateIfNotExistsAsync(
            PublicAccessType publicAccessType = PublicAccessType.None,
            IDictionary<string, string>? metadata = null,
            BlobContainerEncryptionScopeOptions? encryptionScopeOptions = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobContainerInfo(new ETag("created"), DateTimeOffset.UnixEpoch), new CatalogResponse()));

        public override AsyncPageable<BlobItem> GetBlobsAsync(GetBlobsOptions options, CancellationToken cancellationToken = default)
        {
            Assert.Equal("wal/catalog/", options.Prefix);
            var items = new[] { "catalog/a", "catalog/b", "catalog/c" }.Select(id => BlobsModelFactory.BlobItem(
                name: $"wal/{id}", deleted: false,
                properties: BlobsModelFactory.BlobItemProperties(accessTierInferred: false, blobType: BlobType.Append))).ToArray();
            return AsyncPageable<BlobItem>.FromPages(
            [
                Page<BlobItem>.FromValues([], "next", new CatalogResponse()),
                Page<BlobItem>.FromValues(items, null, new CatalogResponse())
            ]);
        }
    }

    private sealed class CatalogTableService : TableServiceClient
    {
        private readonly CatalogTable _table = new();
        public override TableClient GetTableClient(string tableName) => _table;
    }

    private sealed class CatalogTable : TableClient
    {
        public override Task<Response<TableItem>> CreateIfNotExistsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Response.FromValue(new TableItem("journal"), new CatalogResponse()));

        public override AsyncPageable<T> QueryAsync<T>(
            string? filter = null, int? maxPerPage = null, IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
        {
            Assert.Contains("PartitionKey le", filter);
            Assert.Equal(1000, maxPerPage);
            var items = new[] { "catalog/a", "catalog/b", "catalog/c" }
                .Select(id => (T)(ITableEntity)new TableEntity { ["JournalId"] = id }).ToArray();
            return AsyncPageable<T>.FromPages(
            [
                Page<T>.FromValues([], "next", new CatalogResponse()),
                Page<T>.FromValues(items, null, new CatalogResponse())
            ]);
        }
    }

    private sealed class CatalogResponse : Response
    {
        public override int Status => 200;
        public override string ReasonPhrase => "OK";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = "";
        public override void Dispose() { }
        protected override bool ContainsHeader(string name) => false;
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
        protected override bool TryGetHeader(string name, out string value) { value = ""; return false; }
        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values) { values = []; return false; }
    }

    private sealed class FakeScenario(AzureJournalReport report) : IAzureJournalScenario
    {
        public Func<CancellationToken, Task> Prepare { get; init; } = _ => Task.CompletedTask;
        public Func<int, CancellationToken, Task> Execute { get; init; } = (_, _) => Task.CompletedTask;
        public Func<CancellationToken, Task> CleanupAction { get; init; } = _ => Task.CompletedTask;
        public Func<CancellationToken, Task> Verify { get; init; } = _ => Task.CompletedTask;
        public Task PrepareAsync(CancellationToken cancellationToken) => Prepare(cancellationToken);
        public Task ExecuteAsync(int index, CancellationToken cancellationToken) => Execute(index, cancellationToken);
        public Task VerifyAsync(IEnumerable<int> completed, CancellationToken cancellationToken) => Verify(cancellationToken);
        public void StartMetrics() { }
        public IReadOnlyList<ProviderMetric> StopMetrics() => [];
        public async Task CleanupAsync(CancellationToken cancellationToken)
        {
            await CleanupAction(cancellationToken);
            report.Cleanup = "deleted";
        }
    }
}
