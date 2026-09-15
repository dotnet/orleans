using System.Diagnostics.Metrics;
using System.Text.Json;
using Azure;
using DurableJobsJournaling;
using Orleans.Journaling;
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
        AzureJournalScenario.VerifyAccount(AzureJournalBackend.StandardBlob, "StorageV2", "Standard_LRS");
        AzureJournalScenario.VerifyAccount(AzureJournalBackend.PremiumBlob, "BlockBlobStorage", "Premium_LRS");
        Assert.Throws<InvalidOperationException>(() => AzureJournalScenario.VerifyAccount(AzureJournalBackend.PremiumBlob, "StorageV2", "Standard_LRS"));
        Assert.Throws<InvalidOperationException>(() => AzureJournalScenario.VerifyAccount(AzureJournalBackend.PremiumBlob, "Azurite", "Premium_LRS"));
        Assert.Throws<InvalidOperationException>(() => AzureJournalScenario.VerifyAccount(AzureJournalBackend.StandardBlob, "BlockBlobStorage", "Premium_LRS"));
        Assert.True(AzureJournalOptions.Parse(["--backend", "Table", "--allow-azure", "true"]).AllowAzure);
    }

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
        using var meter = new Meter("Microsoft.Orleans");
        using var foreign = new Meter("Microsoft.Orleans");
        var count = meter.CreateCounter<long>("orleans-journaling-provider-operations");
        var duration = meter.CreateHistogram<double>("orleans-journaling-provider-operation-duration", "ms");
        var foreignCount = foreign.CreateCounter<long>(count.Name);
        using var collector = new ProviderMetrics(meter);
        count.Add(100);
        collector.Start();
        foreignCount.Add(1000);
        count.Add(2, new KeyValuePair<string, object?>("operation", "append"));
        duration.Record(5);
        var metrics = collector.Stop();
        count.Add(100);
        Assert.Equal(2, metrics.Count);
        Assert.Equal(2, Assert.Single(metrics, metric => metric.Instrument == count.Name).Sum);
        var histogram = Assert.Single(metrics, metric => metric.Instrument == duration.Name);
        Assert.Equal(5, histogram.Mean);
        Assert.InRange(histogram.P99UpperBound!.Value, 5, 5.5);
        collector.Start();
        Assert.Empty(collector.Stop());
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
            var report = new AzureJournalReport(new() { Output = "secret-path", Operations = 1, Concurrency = 1 });
            report.Failures.Add(BenchmarkFailure.From("setup", new RequestFailedException(403, "sig=secret")));
            await report.ExportAsync(prefix);
            var json = await File.ReadAllTextAsync(prefix + ".json", TestContext.Current.CancellationToken);
            var csv = await File.ReadAllTextAsync(prefix + ".csv", TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(1, document.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal("AzuriteBlob", document.RootElement.GetProperty("Configuration").GetProperty("Backend").GetString());
            Assert.Equal(403, document.RootElement.GetProperty("Failures")[0].GetProperty("HttpStatus").GetInt32());
            Assert.Contains("provider_metrics_json", csv);
            Assert.Contains("payload_bytes_per_second", csv);
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
