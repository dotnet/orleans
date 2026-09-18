using System.Globalization;
using System.Net;
using System.Reflection;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Clustering.DynamoDB;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Xunit;

namespace AWSUtils.Tests.MembershipTests
{
    [TestCategory("Membership"), TestCategory("AWS"), TestCategory("DynamoDb")]
    [TestSuite("BVT")]
    [TestProvider("DynamoDB")]
    [TestArea("Membership")]
    public class DynamoDBMembershipTableUnitTests
    {
        [Theory]
        [InlineData("Initialize")]
        [InlineData("Delete")]
        [InlineData("Cleanup")]
        [InlineData("ReadRow")]
        [InlineData("ReadAll")]
        [InlineData("Insert")]
        [InlineData("Update")]
        [InlineData("Heartbeat")]
        public async Task CanceledOperationsDoNotAccessStorage(string operation)
        {
            var table = new DynamoDBMembershipTable(
                NullLoggerFactory.Instance,
                Options.Create(new DynamoDBClusteringOptions()),
                Options.Create(new ClusterOptions { ClusterId = "cluster" }));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.Cancel();
            var token = cancellation.Token;
            var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
            var entry = new MembershipEntry { SiloAddress = silo };
            var version = new TableVersion(1, "etag");

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
            {
                "Initialize" => table.InitializeMembershipTableAsync(true, token),
                "Delete" => table.DeleteMembershipTableEntriesAsync("cluster", token),
                "Cleanup" => table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.MaxValue, token),
                "ReadRow" => table.ReadRowAsync(silo, token),
                "ReadAll" => table.ReadAllAsync(token),
                "Insert" => table.InsertRowAsync(entry, version, token),
                "Update" => table.UpdateRowAsync(entry, "etag", version, token),
                "Heartbeat" => table.UpdateIAmAliveAsync(entry, token),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            });

            Assert.Equal(token, exception.CancellationToken);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DeletesUseExpectedBackendApiAndForwardCancellation(bool cleanup)
        {
            using var client = new BatchDeleteClient();
            var table = CreateTable(client);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

            var pending = DeleteEntries(table, cleanup, cancellation.Token);
            try
            {
                if (cleanup)
                {
                    Assert.Empty(client.Requests);
                    Assert.Equal(25, client.Deletes.Count);
                    for (var index = 0; index < client.Deletes.Count; index++)
                    {
                        var delete = client.Deletes[index];
                        Assert.Equal("membership", delete.TableName);
                        Assert.Equal("cluster", delete.Key[SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME].S);
                        Assert.Equal($"silo-{index}", delete.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S);
                        Assert.Equal(
                            "SiloStatus = :SiloStatus AND ETag = :ETag AND attribute_not_exists(StartTime)"
                                + " AND IAmAliveTime = :IAmAliveTime AND attribute_not_exists(SuspectingSilos) AND attribute_not_exists(SuspectingTimes)",
                            delete.ConditionExpression);
                        Assert.Equal(3, delete.ExpressionAttributeValues.Count);
                        Assert.Equal(((int)SiloStatus.Dead).ToString(CultureInfo.InvariantCulture), delete.ExpressionAttributeValues[":SiloStatus"].N);
                        Assert.Equal((index + 1).ToString(CultureInfo.InvariantCulture), delete.ExpressionAttributeValues[":ETag"].N);
                        Assert.Equal("2026-01-01 00:00:00.000 GMT", delete.ExpressionAttributeValues[":IAmAliveTime"].S);
                    }
                }
                else
                {
                    Assert.Empty(client.Deletes);
                    Assert.Equal(new[] { 25, 25, 2 }, client.Requests.Select(request => request.RequestItems["membership"].Count));
                    Assert.All(client.Requests.SelectMany(request => request.RequestItems["membership"]),
                        item => Assert.Equal("cluster", item.DeleteRequest.Key[SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME].S));
                    Assert.Single(client.Requests.SelectMany(request => request.RequestItems["membership"]),
                        item => item.DeleteRequest.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S == SiloInstanceRecord.TABLE_VERSION_ROW);
                }

                Assert.Equal(cleanup ? 25 : 3, client.Tokens.Count);
                Assert.All(client.Tokens, token => Assert.Equal(cancellation.Token, token));
                Assert.False(pending.IsCompleted);
            }
            finally
            {
                client.CompleteAll();
                await pending;
            }

            if (cleanup)
            {
                Assert.Equal(51, client.Deletes.Count);
                Assert.All(client.Tokens, token => Assert.Equal(cancellation.Token, token));
                Assert.Equal(7, client.Version);
                Assert.Equal(7, client.VersionEtag);
                Assert.Empty(client.Records);
            }
        }

        [Fact]
        public async Task CleanupBoundsConcurrentDeletesAcrossBatches()
        {
            var secondBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thirdBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = new BatchDeleteClient
            {
                OnDeleteRequest = count =>
                {
                    if (count == 50)
                    {
                        secondBatch.SetResult();
                    }
                    else if (count == 51)
                    {
                        thirdBatch.SetResult();
                    }
                }
            };
            var pending = DeleteEntries(CreateTable(client), cleanup: true, TestContext.Current.CancellationToken);
            try
            {
                Assert.Equal(25, client.Deletes.Count);
                Assert.Equal(25, client.PendingDeletes);
                Assert.False(pending.IsCompleted);

                client.CompletePendingDeletes();
                await secondBatch.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.Equal(50, client.Deletes.Count);
                Assert.Equal(25, client.PendingDeletes);
                Assert.False(pending.IsCompleted);

                client.CompletePendingDeletes();
                await thirdBatch.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.Equal(51, client.Deletes.Count);
                Assert.Equal(1, client.PendingDeletes);
                Assert.False(pending.IsCompleted);
            }
            finally
            {
                client.CompleteAll();
                await pending;
            }

            Assert.Equal(25, client.PeakPendingDeletes);
            Assert.Empty(client.Records);
            Assert.Equal(7, client.Version);
            Assert.Equal(7, client.VersionEtag);
        }

        [Fact]
        public async Task CleanupCancellationDrainsCurrentBatchBeforeCompleting()
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            using var client = new BatchDeleteClient
            {
                OnDeleteRequest = count =>
                {
                    if (count == 25)
                    {
                        cancellation.Cancel();
                    }
                }
            };
            var pending = DeleteEntries(CreateTable(client), cleanup: true, cancellation.Token);
            try
            {
                Assert.Equal(25, client.Deletes.Count);
                Assert.Equal(25, client.PendingDeletes);
                Assert.False(pending.IsCompleted);
            }
            finally
            {
                client.CompleteAll();
                var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                Assert.Equal(cancellation.Token, exception.CancellationToken);
            }

            Assert.Equal(25, client.Deletes.Count);
            Assert.Equal(26, client.Records.Count);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchCancellationWaitsForAlreadyStartedDeletes(bool cleanup)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            using var client = new BatchDeleteClient { OnFirstDeleteRequest = cancellation.Cancel };
            var table = CreateTable(client);

            var pending = DeleteEntries(table, cleanup, cancellation.Token);
            try
            {
                if (cleanup)
                {
                    Assert.Single(client.Deletes);
                    Assert.Empty(client.Requests);
                }
                else
                {
                    Assert.Single(client.Requests);
                    Assert.Empty(client.Deletes);
                }

                Assert.Equal(cancellation.Token, Assert.Single(client.Tokens));
                Assert.False(pending.IsCompleted);
            }
            finally
            {
                client.CompleteAll();
                var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                Assert.Equal(cancellation.Token, exception.CancellationToken);
            }
        }

        [Theory]
        [InlineData(nameof(SiloInstanceRecord.IAmAliveTime), false)]
        [InlineData(nameof(SiloInstanceRecord.StartTime), false)]
        [InlineData(nameof(SiloInstanceRecord.SuspectingTimes), false)]
        [InlineData(nameof(SiloInstanceRecord.SuspectingTimes), true)]
        [InlineData(nameof(SiloInstanceRecord.ETag), false)]
        [InlineData(nameof(SiloInstanceRecord.Status), false)]
        public async Task CleanupConditionalConflictsPreserveChangedRows(string field, bool existingVote)
        {
            using var client = new BatchDeleteClient();
            if (existingVote)
            {
                client.Records["silo-0"].SuspectingSilos = "127.0.0.1:11112@1";
                client.Records["silo-0"].SuspectingTimes = "2026-01-01 00:00:00.000 GMT";
            }

            client.OnFirstDeleteRequest = () =>
            {
                var row = client.Records["silo-0"];
                const string recent = "2026-01-03 00:00:00.000 GMT";
                switch (field)
                {
                    case nameof(SiloInstanceRecord.IAmAliveTime): row.IAmAliveTime = recent; break;
                    case nameof(SiloInstanceRecord.StartTime): row.StartTime = recent; break;
                    case nameof(SiloInstanceRecord.SuspectingTimes):
                        row.SuspectingSilos = "127.0.0.1:11112@1";
                        row.SuspectingTimes = recent;
                        break;
                    case nameof(SiloInstanceRecord.ETag): row.ETag++; break;
                    case nameof(SiloInstanceRecord.Status): row.Status = (int)SiloStatus.Active; break;
                }
            };
            client.CompleteAll();
            var table = CreateTable(client);

            await DeleteEntries(table, cleanup: true, TestContext.Current.CancellationToken);

            Assert.Empty(client.Requests);
            Assert.Equal(51, client.Deletes.Count);
            Assert.Equal(7, client.Version);
            Assert.Equal(7, client.VersionEtag);
            Assert.Equal("silo-0", Assert.Single(client.Records).Key);
            Assert.Equal(1, client.QueryCount);
        }

        [Theory]
        [InlineData("resource")]
        [InlineData("authorization")]
        [InlineData("network")]
        public async Task CleanupStorageFailuresPropagateAfterStartedDeletesComplete(string kind)
        {
            var failure = CreateStorageFailure(kind);
            using var client = new BatchDeleteClient { DeleteFailure = failure };
            var table = CreateTable(client);

            var pending = table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.MaxValue, TestContext.Current.CancellationToken);
            try
            {
                Assert.False(pending.IsCompleted);
                Assert.Equal(25, client.Deletes.Count);
            }
            finally
            {
                client.CompleteAll();
            }

            var exception = await Assert.ThrowsAnyAsync<Exception>(() => pending);
            Assert.Same(failure, exception);
            Assert.Empty(client.Requests);
            Assert.Equal(7, client.Version);
            Assert.Equal(
                new[] { "silo-0" }.Concat(Enumerable.Range(25, 26).Select(i => $"silo-{i}")).Order(),
                client.Records.Keys.Order());
            Assert.Equal(25, client.Deletes.Count);
        }

        [Fact]
        public async Task CleanupDeletesEveryEligibleRowAndPreservesVersion()
        {
            using var client = new BatchDeleteClient { RowCount = 201 };
            client.CompleteAll();
            var table = CreateTable(client);

            await DeleteEntries(table, cleanup: true, TestContext.Current.CancellationToken);

            Assert.Equal(201, client.Deletes.Count);
            Assert.Equal(7, client.Version);
            Assert.Equal(7, client.VersionEtag);
            Assert.Empty(client.Records);
            Assert.Empty(client.Requests);
            Assert.Equal(1, client.QueryCount);
        }

        [Fact]
        public async Task CleanupToleratesAlreadyDeletedRows()
        {
            using var client = new BatchDeleteClient { RowCount = 1 };
            client.OnFirstDeleteRequest = () => client.Records.Remove("silo-0");
            client.CompleteAll();
            var table = CreateTable(client);

            await DeleteEntries(table, cleanup: true, TestContext.Current.CancellationToken);

            Assert.Single(client.Deletes);
            Assert.Empty(client.Records);
            Assert.Equal(7, client.Version);
            Assert.Equal(7, client.VersionEtag);
        }

        [Theory]
        [InlineData(nameof(SiloInstanceRecord.StartTime))]
        [InlineData(nameof(SiloInstanceRecord.IAmAliveTime))]
        [InlineData(nameof(SiloInstanceRecord.SuspectingTimes))]
        public async Task CleanupUsesExclusiveTickPrecisionCutoff(string timestampField)
        {
            var cutoff = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);
            using var client = new BatchDeleteClient { RowCount = 1 };
            var row = client.Records["silo-0"];
            const string timestamp = "2026-01-03 00:00:00.000 GMT";
            switch (timestampField)
            {
                case nameof(SiloInstanceRecord.StartTime): row.StartTime = timestamp; break;
                case nameof(SiloInstanceRecord.IAmAliveTime): row.IAmAliveTime = timestamp; break;
                case nameof(SiloInstanceRecord.SuspectingTimes):
                    row.SuspectingSilos = "127.0.0.1:11112@1|127.0.0.1:11113@1";
                    row.SuspectingTimes = $"{timestamp}|2026-01-01 00:00:00.000 GMT";
                    break;
            }
            client.CompleteAll();
            var table = CreateTable(client);

            await table.CleanupDefunctSiloEntriesAsync(cutoff, TestContext.Current.CancellationToken);
            Assert.Empty(client.Deletes);
            Assert.Equal(7, client.Version);
            Assert.Single(client.Records);
            await table.CleanupDefunctSiloEntriesAsync(cutoff.AddTicks(1), TestContext.Current.CancellationToken);

            Assert.Single(client.Deletes);
            Assert.Empty(client.Records);
            Assert.Equal(7, client.Version);
            Assert.Equal(7, client.VersionEtag);
        }

        [Theory]
        [InlineData("127.0.0.1:11112@1", null)]
        [InlineData(null, "2026-01-01 00:00:00.000 GMT")]
        [InlineData("127.0.0.1:11112@1|127.0.0.1:11113@1", "2026-01-01 00:00:00.000 GMT")]
        [InlineData("127.0.0.1:11112@1", "2026-01-01 00:00:00.000 GMT|2026-01-01 00:00:00.000 GMT")]
        public async Task CleanupRejectsMismatchedSuspicionLists(string? silos, string? times)
        {
            using var client = new BatchDeleteClient { RowCount = 1 };
            client.Records["silo-0"].SuspectingSilos = silos;
            client.Records["silo-0"].SuspectingTimes = times;

            var exception = await Assert.ThrowsAsync<OrleansException>(() =>
                DeleteEntries(CreateTable(client), cleanup: true, TestContext.Current.CancellationToken));

            Assert.Contains("SuspectingSilos.Length", exception.Message);
            Assert.Contains("SuspectingTimes.Length", exception.Message);
            Assert.Contains("silo-0", exception.Message);
            Assert.Empty(client.Deletes);
            Assert.Single(client.Records);
        }

        [Theory]
        [InlineData("invalid-address", "2026-01-01 00:00:00.000 GMT")]
        [InlineData("127.0.0.1:11112@1|", "2026-01-01 00:00:00.000 GMT|2026-01-01 00:00:00.000 GMT")]
        [InlineData("127.0.0.1:11112@1", "invalid-time")]
        [InlineData("127.0.0.1:11112@1|127.0.0.1:11113@1", "2026-01-01 00:00:00.000 GMT|")]
        public async Task CleanupRejectsMalformedSuspicionValues(string silos, string times)
        {
            using var client = new BatchDeleteClient { RowCount = 1 };
            client.Records["silo-0"].SuspectingSilos = silos;
            client.Records["silo-0"].SuspectingTimes = times;

            await Assert.ThrowsAsync<FormatException>(() =>
                DeleteEntries(CreateTable(client), cleanup: true, TestContext.Current.CancellationToken));

            Assert.Empty(client.Deletes);
            Assert.Single(client.Records);
        }

        [Fact]
        public async Task CleanupDrainsStartedBatchBeforeSurfacingMalformedLaterRow()
        {
            using var client = new BatchDeleteClient();
            client.Records["silo-25"].SuspectingSilos = "127.0.0.1:11112@1";
            var pending = DeleteEntries(CreateTable(client), cleanup: true, TestContext.Current.CancellationToken);
            try
            {
                Assert.Equal(25, client.Deletes.Count);
                Assert.Equal(25, client.PendingDeletes);
                Assert.False(pending.IsCompleted);
            }
            finally
            {
                client.CompleteAll();
                await Assert.ThrowsAsync<OrleansException>(() => pending);
            }

            Assert.Equal(25, client.Deletes.Count);
            Assert.Equal(26, client.Records.Count);
            Assert.Contains("silo-25", client.Records.Keys);
        }

        [Theory]
        [InlineData(SiloStatus.Created)]
        [InlineData(SiloStatus.Joining)]
        [InlineData(SiloStatus.Active)]
        [InlineData(SiloStatus.ShuttingDown)]
        [InlineData(SiloStatus.Stopping)]
        [InlineData(SiloStatus.Dead)]
        public async Task CleanupRemovesOnlyDeadRows(SiloStatus status)
        {
            using var client = new BatchDeleteClient { RowCount = 1 };
            client.Records["silo-0"].Status = (int)status;
            client.CompleteAll();
            var table = CreateTable(client);

            await DeleteEntries(table, cleanup: true, TestContext.Current.CancellationToken);

            Assert.Empty(client.Requests);
            Assert.Equal(status == SiloStatus.Dead ? 1 : 0, client.Deletes.Count);
            Assert.Equal(status == SiloStatus.Dead ? 0 : 1, client.Records.Count);
            Assert.Equal(7, client.Version);
            Assert.Equal(7, client.VersionEtag);
        }

        private static Task DeleteEntries(DynamoDBMembershipTable table, bool cleanup, CancellationToken cancellationToken) =>
            cleanup
                ? table.CleanupDefunctSiloEntriesAsync(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), cancellationToken)
                : table.DeleteMembershipTableEntriesAsync("cluster", cancellationToken);

        [Fact]
        public async Task HeartbeatAfterCleanupPreservesAbsenceAndVersion()
        {
            using var client = new HeartbeatClient();
            var table = CreateTable(client);

            await table.UpdateIAmAliveAsync(new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = DateTime.UnixEpoch
            }, TestContext.Current.CancellationToken);

            Assert.Single(client.Updates);
            Assert.Contains("attribute_exists(DeploymentId)", client.Updates[0].ConditionExpression);
            Assert.Contains("attribute_exists(SiloIdentity)", client.Updates[0].ConditionExpression);
            Assert.Equal(new[] { "127.0.0.1-11111-1", SiloInstanceRecord.TABLE_VERSION_ROW },
                client.Reads.Select(request => request.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S));
            Assert.Equal(7, client.Version.MembershipVersion);
            Assert.Equal(7, client.Version.ETag);
        }

        [Theory]
        [InlineData("resource")]
        [InlineData("authorization")]
        [InlineData("network")]
        public async Task HeartbeatInfrastructureFailuresRemainVisible(string kind)
        {
            var failure = CreateStorageFailure(kind);
            using var client = new HeartbeatClient { Failure = failure };
            var table = CreateTable(client);

            var exception = await Assert.ThrowsAnyAsync<Exception>(() => table.UpdateIAmAliveAsync(new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = DateTime.UnixEpoch
            }, TestContext.Current.CancellationToken));

            Assert.Same(failure, exception);
            Assert.Single(client.Updates);
            Assert.Empty(client.Reads);
        }

        [Fact]
        public async Task HeartbeatMissingMembershipHistoryFailsClosed()
        {
            using var client = new HeartbeatClient { VersionExists = false };
            var table = CreateTable(client);

            await Assert.ThrowsAsync<KeyNotFoundException>(() => table.UpdateIAmAliveAsync(new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = DateTime.UnixEpoch
            }, TestContext.Current.CancellationToken));

            Assert.Single(client.Updates);
            Assert.Equal(2, client.Reads.Count);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(1)]
        public async Task HeartbeatsPreserveMaximumTimestampAndLogicalVersions(int offset)
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            using var client = new HeartbeatClient { Current = CreateRecord() };
            var before = client.Current.GetFields(includeKeys: true);
            var table = CreateTable(client);

            await table.UpdateIAmAliveAsync(new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = now.AddSeconds(offset)
            }, TestContext.Current.CancellationToken);

            Assert.Equal(LogFormatter.PrintDate(now.AddSeconds(Math.Max(offset, 0))), client.Current.IAmAliveTime);
            var after = client.Current.GetFields(includeKeys: true);
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            foreach (var field in before.Keys.Where(key => key != SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME))
            {
                Assert.Equal(before[field].N, after[field].N);
                Assert.Equal(before[field].S, after[field].S);
            }
            Assert.Single(client.Updates);
            Assert.Equal(offset > 0 ? 0 : 1, client.Reads.Count);
            Assert.Equal(7, client.Version.MembershipVersion);
            Assert.Equal(7, client.Version.ETag);
        }

        [Theory]
        [InlineData("resource")]
        [InlineData("authorization")]
        [InlineData("network")]
        public async Task HeartbeatVerificationReadFailuresRemainVisible(string kind)
        {
            var failure = CreateStorageFailure(kind);
            using var client = new HeartbeatClient { ReadFailure = failure };
            var table = CreateTable(client);

            var exception = await Assert.ThrowsAnyAsync<Exception>(() => table.UpdateIAmAliveAsync(new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = DateTime.UnixEpoch
            }, TestContext.Current.CancellationToken));

            Assert.Same(failure, exception);
            Assert.Single(client.Updates);
            Assert.Single(client.Reads);
        }

        [Theory]
        [InlineData("number")]
        [InlineData("null")]
        [InlineData("boolean")]
        [InlineData("map")]
        [InlineData("list")]
        [InlineData("string-set")]
        [InlineData("invalid-date")]
        public async Task HeartbeatRejectsMalformedPresentAttributeWithoutRetrying(string kind)
        {
            var heartbeat = kind switch
            {
                "number" => new AttributeValue { N = "123" },
                "null" => new AttributeValue { NULL = true },
                "boolean" => new AttributeValue { BOOL = true },
                "map" => new AttributeValue { M = new() { ["value"] = new AttributeValue("timestamp") } },
                "list" => new AttributeValue { L = [new AttributeValue("timestamp")] },
                "string-set" => new AttributeValue { SS = ["timestamp"] },
                "invalid-date" => new AttributeValue("not-a-date"),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            var fields = CreateRecord().GetFields(includeKeys: true);
            fields[SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME] = heartbeat;
            var updates = 0;
            using var client = new RequestClient
            {
                Update = request =>
                {
                    Assert.Equal(1, ++updates);
                    Assert.Contains("attribute_not_exists(IAmAliveTime)", request.ConditionExpression);
                    Assert.Contains("IAmAliveTime < :IAmAliveTime", request.ConditionExpression);
                    throw new ConditionalCheckFailedException("Present heartbeat fails the string comparison.");
                },
                Read = request =>
                {
                    Assert.True(request.ConsistentRead);
                    Assert.Equal("127.0.0.1-11111-1", request.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S);
                    return new GetItemResponse { Item = fields };
                }
            };

            var exception = await Assert.ThrowsAsync<FormatException>(() => CreateTable(client).UpdateIAmAliveAsync(new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = DateTime.UnixEpoch
            }, TestContext.Current.CancellationToken));

            Assert.Contains("IAmAliveTime", exception.Message);
            Assert.Equal(1, updates);
            Assert.Equal(1, client.ReadCount);
            Assert.Same(heartbeat, fields[SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME]);
        }

        [Theory]
        [InlineData(null, 2)]
        [InlineData("2025-12-31 00:00:00.000 GMT", 2)]
        [InlineData("2026-01-01 00:00:00.000 GMT", 1)]
        [InlineData("2026-01-02 00:00:00.000 GMT", 1)]
        public async Task HeartbeatRechecksAbsentOrValidAttributeAfterContention(string? heartbeat, int expectedUpdates)
        {
            var row = CreateRecord();
            row.IAmAliveTime = heartbeat;
            var fields = row.GetFields(includeKeys: true);
            var updates = 0;
            using var client = new RequestClient
            {
                Update = request =>
                {
                    Assert.True(++updates <= expectedUpdates);
                    if (updates == 1)
                    {
                        throw new ConditionalCheckFailedException("Row changed before the verification read.");
                    }

                    var next = request.ExpressionAttributeValues[":IAmAliveTime"];
                    Assert.True(!fields.TryGetValue(SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME, out var current)
                        || string.CompareOrdinal(current.S, next.S) < 0);
                    fields[SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME] = next;
                    return new UpdateItemResponse { Attributes = new() { [SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME] = next } };
                },
                Read = _ => new GetItemResponse { Item = fields }
            };

            await CreateTable(client).UpdateIAmAliveAsync(new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }, TestContext.Current.CancellationToken);

            Assert.Equal(expectedUpdates, updates);
            Assert.Equal(1, client.ReadCount);
            Assert.Equal(expectedUpdates == 2 ? "2026-01-01 00:00:00.000 GMT" : heartbeat,
                fields[SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME].S);
            Assert.Equal("3", fields[SiloInstanceRecord.ETAG_PROPERTY_NAME].N);
            Assert.Equal("7", fields[SiloInstanceRecord.MEMBERSHIP_VERSION_PROPERTY_NAME].N);
        }

        [Fact]
        public async Task HeartbeatRetryHonorsCancellationAfterVerificationRead()
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var row = CreateRecord();
            row.IAmAliveTime = null;
            var updates = 0;
            using var client = new RequestClient
            {
                Token = cancellation.Token,
                Update = _ =>
                {
                    Assert.Equal(1, ++updates);
                    throw new ConditionalCheckFailedException("Row changed before the verification read.");
                },
                Read = _ =>
                {
                    cancellation.Cancel();
                    return new GetItemResponse { Item = row.GetFields(includeKeys: true) };
                }
            };

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateTable(client).UpdateIAmAliveAsync(new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = DateTime.UnixEpoch
            }, cancellation.Token));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Equal(1, updates);
            Assert.Equal(1, client.ReadCount);
        }

        [Fact]
        public async Task HeartbeatInitializesAbsentAttribute()
        {
            using var client = new HeartbeatClient { Current = CreateRecord() };
            client.Current.IAmAliveTime = null;

            await CreateTable(client).UpdateIAmAliveAsync(new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = DateTime.UnixEpoch
            }, TestContext.Current.CancellationToken);

            Assert.Equal("1970-01-01 00:00:00.000 GMT", client.Current.IAmAliveTime);
            Assert.Single(client.Updates);
            Assert.Empty(client.Reads);
            Assert.Equal(3, client.Current.ETag);
            Assert.Equal(7, client.Current.MembershipVersion);
        }

        public static IEnumerable<object[]> MalformedRecencyAttributes()
        {
            foreach (var operation in new[] { "UpdateRow", "ReadRow", "ReadAll", "Cleanup", "Heartbeat" })
            {
                foreach (var attribute in new[]
                {
                    SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME,
                    SiloInstanceRecord.START_TIME_PROPERTY_NAME,
                    SiloInstanceRecord.SUSPECTING_SILOS_PROPERTY_NAME,
                    SiloInstanceRecord.SUSPECTING_TIMES_PROPERTY_NAME
                })
                {
                    yield return [operation, attribute, "number"];
                    yield return [operation, attribute, "null"];
                    if (attribute is SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME or SiloInstanceRecord.START_TIME_PROPERTY_NAME)
                    {
                        yield return [operation, attribute, "invalid-date"];
                        yield return [operation, attribute, "empty-date"];
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(MalformedRecencyAttributes))]
        public async Task MembershipOperationsRejectMalformedPresentRecencyAttributes(string operation, string attribute, string corruption)
        {
            var row = CreateRecord();
            row.Status = (int)SiloStatus.Dead;
            var fields = row.GetFields(includeKeys: true);
            var invalid = corruption switch
            {
                "number" => new AttributeValue { N = "123" },
                "null" => new AttributeValue { NULL = true },
                "invalid-date" => new AttributeValue("not-a-date"),
                "empty-date" => new AttributeValue(string.Empty),
                _ => throw new ArgumentOutOfRangeException(nameof(corruption))
            };
            fields[attribute] = invalid;
            var updates = 0;
            using var client = new RequestClient
            {
                Read = request => new GetItemResponse
                {
                    Item = request.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S == SiloInstanceRecord.TABLE_VERSION_ROW
                        ? CreateVersion(7).GetFields(true)
                        : fields
                },
                ReadTransaction = _ => new TransactGetItemsResponse
                {
                    Responses = [new ItemResponse { Item = fields }, new ItemResponse { Item = CreateVersion(7).GetFields(true) }]
                },
                Query = _ => new QueryResponse { Items = [CreateVersion(7).GetFields(true), fields], LastEvaluatedKey = [] },
                Update = _ =>
                {
                    Assert.Equal(1, ++updates);
                    throw new ConditionalCheckFailedException("Row requires verification.");
                }
            };
            var table = CreateTable(client);
            var entry = new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = DateTime.UnixEpoch
            };

            var exception = await Assert.ThrowsAsync<FormatException>(() => operation switch
            {
                "UpdateRow" => table.UpdateRowAsync(entry, "3", new TableVersion(8, "7"), TestContext.Current.CancellationToken),
                "ReadRow" => table.ReadRowAsync(entry.SiloAddress, TestContext.Current.CancellationToken),
                "ReadAll" => table.ReadAllAsync(TestContext.Current.CancellationToken),
                "Cleanup" => table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.MaxValue, TestContext.Current.CancellationToken),
                "Heartbeat" => table.UpdateIAmAliveAsync(entry, TestContext.Current.CancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            });

            Assert.Contains(attribute, exception.Message);
            Assert.Contains(row.SiloIdentity, exception.Message);
            Assert.Equal(0, client.WriteCount);
            Assert.Equal(0, client.DeleteCount);
            Assert.Equal(operation == "Heartbeat" ? 1 : 0, updates);
            Assert.Same(invalid, fields[attribute]);
        }

        [Theory]
        [InlineData("UpdateRow")]
        [InlineData("ReadRow")]
        [InlineData("ReadAll")]
        [InlineData("Cleanup")]
        [InlineData("Heartbeat")]
        public async Task MembershipOperationsPreserveAbsentLegacyRecencyAttributes(string operation)
        {
            var row = CreateRecord();
            row.Status = (int)SiloStatus.Dead;
            row.IAmAliveTime = null;
            row.StartTime = null;
            var fields = row.GetFields(includeKeys: true);
            var updates = 0;
            using var client = new RequestClient
            {
                Read = request => new GetItemResponse
                {
                    Item = request.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S == SiloInstanceRecord.TABLE_VERSION_ROW
                        ? CreateVersion(7).GetFields(true)
                        : fields
                },
                ReadTransaction = _ => new TransactGetItemsResponse
                {
                    Responses = [new ItemResponse { Item = fields }, new ItemResponse { Item = CreateVersion(7).GetFields(true) }]
                },
                Query = _ => new QueryResponse { Items = [CreateVersion(7).GetFields(true), fields], LastEvaluatedKey = [] },
                Write = request =>
                {
                    var put = Assert.IsType<Put>(request.TransactItems[0].Put);
                    Assert.Equal("ETag = :currentETag AND attribute_not_exists(IAmAliveTime)", put.ConditionExpression);
                    Assert.Equal("1970-01-01 00:00:00.000 GMT", put.Item[SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME].S);
                    Assert.False(put.ExpressionAttributeValues.ContainsKey(":currentHeartbeat"));
                    return new TransactWriteItemsResponse();
                },
                Update = request =>
                {
                    Assert.True(++updates <= 2);
                    if (updates == 1)
                    {
                        throw new ConditionalCheckFailedException("Row changed before verification.");
                    }
                    Assert.False(fields.ContainsKey(SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME));
                    var heartbeat = request.ExpressionAttributeValues[":IAmAliveTime"];
                    fields[SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME] = heartbeat;
                    return new UpdateItemResponse { Attributes = new() { [SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME] = heartbeat } };
                }
            };
            var table = CreateTable(client);
            var entry = new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = DateTime.UnixEpoch
            };

            switch (operation)
            {
                case "UpdateRow":
                    Assert.True(await table.UpdateRowAsync(entry, "3", new TableVersion(8, "7"), TestContext.Current.CancellationToken));
                    break;
                case "ReadRow":
                case "ReadAll":
                    var result = operation == "ReadRow"
                        ? await table.ReadRowAsync(entry.SiloAddress, TestContext.Current.CancellationToken)
                        : await table.ReadAllAsync(TestContext.Current.CancellationToken);
                    var member = Assert.Single(result.Members).Item1;
                    Assert.Equal(default, member.IAmAliveTime);
                    Assert.Equal(default, member.StartTime);
                    Assert.True(member.SuspectTimes is null || member.SuspectTimes.Count == 0);
                    Assert.Equal(7, result.Version.Version);
                    break;
                case "Cleanup":
                    await table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.MaxValue, TestContext.Current.CancellationToken);
                    Assert.False(fields.ContainsKey(SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME));
                    break;
                case "Heartbeat":
                    await table.UpdateIAmAliveAsync(entry, TestContext.Current.CancellationToken);
                    Assert.Equal("1970-01-01 00:00:00.000 GMT", fields[SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME].S);
                    break;
            }

            Assert.Equal(operation == "UpdateRow" ? 1 : 0, client.WriteCount);
            Assert.Equal(operation == "Heartbeat" ? 2 : 0, updates);
            Assert.Equal(0, client.DeleteCount);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task MembershipWritesAtomicallyConditionRowAndTableVersion(bool update, bool newerHeartbeat)
        {
            var current = CreateRecord();
            current.SuspectingSilos = "127.0.0.1:11112@1";
            current.SuspectingTimes = current.IAmAliveTime;
            if (newerHeartbeat)
            {
                current.IAmAliveTime = "2026-01-01 00:00:10.000 GMT";
            }

            using var client = new RequestClient
            {
                Read = request =>
                {
                    Assert.True(request.ConsistentRead);
                    Assert.Equal(current.SiloIdentity, request.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S);
                    return new GetItemResponse { Item = current.GetFields(includeKeys: true) };
                },
                Write = request =>
                {
                    Assert.Equal(2, request.TransactItems.Count);
                    var put = Assert.IsType<Put>(request.TransactItems[0].Put);
                    var version = Assert.IsType<Update>(request.TransactItems[1].Update);
                    Assert.Equal("membership", put.TableName);
                    Assert.Equal("membership", version.TableName);
                    Assert.Equal("cluster", put.Item[SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME].S);
                    Assert.Equal(current.SiloIdentity, put.Item[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S);
                    Assert.Equal("replacement-host", put.Item[SiloInstanceRecord.HOSTNAME_PROPERTY_NAME].S);
                    Assert.Equal("replacement-silo", put.Item[SiloInstanceRecord.SILO_NAME_PROPERTY_NAME].S);
                    Assert.Equal("30000", put.Item[SiloInstanceRecord.PROXY_PORT_PROPERTY_NAME].N);
                    Assert.Equal(((int)SiloStatus.Dead).ToString(CultureInfo.InvariantCulture), put.Item[SiloInstanceRecord.STATUS_PROPERTY_NAME].N);
                    Assert.Equal("8", put.Item[SiloInstanceRecord.MEMBERSHIP_VERSION_PROPERTY_NAME].N);
                    Assert.Equal(update ? "4" : "0", put.Item[SiloInstanceRecord.ETAG_PROPERTY_NAME].N);
                    Assert.Equal(newerHeartbeat ? current.IAmAliveTime : "2026-01-01 00:00:01.000 GMT",
                        put.Item[SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME].S);
                    Assert.False(put.Item.ContainsKey(SiloInstanceRecord.SUSPECTING_SILOS_PROPERTY_NAME));
                    Assert.False(put.Item.ContainsKey(SiloInstanceRecord.SUSPECTING_TIMES_PROPERTY_NAME));
                    if (update)
                    {
                        Assert.Equal("ETag = :currentETag AND IAmAliveTime = :currentHeartbeat", put.ConditionExpression);
                        Assert.Equal("3", put.ExpressionAttributeValues[":currentETag"].N);
                        Assert.Equal(current.IAmAliveTime, put.ExpressionAttributeValues[":currentHeartbeat"].S);
                    }
                    else
                    {
                        Assert.Equal("attribute_not_exists(DeploymentId) AND attribute_not_exists(SiloIdentity)", put.ConditionExpression);
                    }
                    Assert.Equal("VersionRow", version.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S);
                    Assert.Equal("cluster", version.Key[SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME].S);
                    Assert.Equal("ETag = :currentETag", version.ConditionExpression);
                    Assert.Equal("7", version.ExpressionAttributeValues[":currentETag"].N);
                    Assert.Equal("8", version.ExpressionAttributeValues[":MembershipVersion"].N);
                    Assert.Equal("8", version.ExpressionAttributeValues[":ETag"].N);
                    Assert.Contains("MembershipVersion = :MembershipVersion", version.UpdateExpression);
                    Assert.Contains("ETag = :ETag", version.UpdateExpression);
                    return new TransactWriteItemsResponse();
                }
            };
            var table = CreateTable(client);
            var entry = new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                HostName = "replacement-host",
                SiloName = "replacement-silo",
                ProxyPort = 30000,
                Status = SiloStatus.Dead,
                StartTime = DateTime.UnixEpoch,
                IAmAliveTime = new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc),
                SuspectTimes = []
            };

            Assert.True(update
                ? await table.UpdateRowAsync(entry, "3", new TableVersion(8, "7"), TestContext.Current.CancellationToken)
                : await table.InsertRowAsync(entry, new TableVersion(8, "7"), TestContext.Current.CancellationToken));
            Assert.Equal(1, client.WriteCount);
            Assert.Equal(update ? 1 : 0, client.ReadCount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task UpdateRejectsMissingOrStaleRowsBeforeWriting(bool missing)
        {
            using var client = new RequestClient
            {
                Read = _ => missing ? new GetItemResponse() : new GetItemResponse { Item = CreateRecord().GetFields(true) }
            };
            var table = CreateTable(client);

            Assert.False(await table.UpdateRowAsync(new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1)
            }, "2", new TableVersion(8, "7"), TestContext.Current.CancellationToken));
            Assert.Equal(1, client.ReadCount);
            Assert.Equal(0, client.WriteCount);
        }

        [Fact]
        public async Task UpdateRejectsConcurrentHeartbeatWithoutOverwritingIt()
        {
            var row = CreateRecord();
            using var client = new RequestClient
            {
                Read = _ => new GetItemResponse { Item = row.GetFields(includeKeys: true) },
                Write = request =>
                {
                    row.IAmAliveTime = "2026-01-01 00:00:10.000 GMT";
                    var put = Assert.IsType<Put>(request.TransactItems[0].Put);
                    Assert.Equal("ETag = :currentETag AND IAmAliveTime = :currentHeartbeat", put.ConditionExpression);
                    Assert.NotEqual(row.IAmAliveTime, put.ExpressionAttributeValues[":currentHeartbeat"].S);
                    throw new TransactionCanceledException("Row condition failed.")
                    {
                        CancellationReasons = [new() { Code = "ConditionalCheckFailed" }, new() { Code = "None" }]
                    };
                }
            };
            var table = CreateTable(client);

            Assert.False(await table.UpdateRowAsync(new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc)
            }, "3", new TableVersion(8, "7"), TestContext.Current.CancellationToken));
            Assert.Equal("2026-01-01 00:00:10.000 GMT", row.IAmAliveTime);
            Assert.Equal(3, row.ETag);
            Assert.Equal(7, row.MembershipVersion);
            Assert.Equal(1, client.WriteCount);
        }

        [Theory]
        [InlineData(false, "ConditionalCheckFailed")]
        [InlineData(true, "ConditionalCheckFailed")]
        [InlineData(false, "TransactionConflict")]
        [InlineData(true, "TransactionConflict")]
        [InlineData(false, "ValidationError")]
        [InlineData(true, "ValidationError")]
        [InlineData(false, "MissingReasons")]
        [InlineData(true, "MissingReasons")]
        public async Task MembershipWritesDistinguishContentionFromStorageFailure(bool update, string reason)
        {
            var failure = new TransactionCanceledException("ConditionalCheckFailed may be one of several causes.")
            {
                CancellationReasons = reason == "MissingReasons" ? null : [new() { Code = "ConditionalCheckFailed" }, new() { Code = reason }]
            };
            using var client = new RequestClient
            {
                Read = _ => new GetItemResponse { Item = CreateRecord().GetFields(true) },
                Write = _ => throw failure
            };
            var table = CreateTable(client);
            var entry = new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = DateTime.UnixEpoch
            };
            var pending = update
                ? table.UpdateRowAsync(entry, "3", new TableVersion(8, "7"), TestContext.Current.CancellationToken)
                : table.InsertRowAsync(entry, new TableVersion(8, "7"), TestContext.Current.CancellationToken);

            if (reason is "ValidationError" or "MissingReasons")
            {
                Assert.Same(failure, await Assert.ThrowsAsync<TransactionCanceledException>(() => pending));
            }
            else
            {
                Assert.False(await pending);
            }
            Assert.Equal(1, client.WriteCount);
        }

        [Fact]
        public async Task ReadAllRetriesWhenVersionChangesAcrossPages()
        {
            var reads = 0;
            var queries = 0;
            var continuation = CreateRecord().GetKeys();
            using var client = new RequestClient
            {
                Read = request =>
                {
                    Assert.True(request.ConsistentRead);
                    Assert.Equal("VersionRow", request.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S);
                    return new GetItemResponse { Item = CreateVersion(++reads == 1 ? 7 : 8).GetFields(true) };
                },
                Query = request =>
                {
                    Assert.True(request.ConsistentRead);
                    Assert.Equal("DeploymentId = :DeploymentId", request.KeyConditionExpression);
                    Assert.Equal("cluster", request.ExpressionAttributeValues[":DeploymentId"].S);
                    queries++;
                    var firstPage = queries % 2 == 1;
                    if (firstPage)
                    {
                        Assert.True(request.ExclusiveStartKey is null || request.ExclusiveStartKey.Count == 0);
                    }
                    else
                    {
                        Assert.Equal(continuation, request.ExclusiveStartKey);
                    }
                    var row = CreateRecord();
                    row.MembershipVersion = queries <= 2 ? 7 : 8;
                    row.HostName = queries <= 2 ? "old" : "current";
                    return new QueryResponse
                    {
                        Items = [firstPage ? CreateVersion(row.MembershipVersion).GetFields(true) : row.GetFields(true)],
                        LastEvaluatedKey = firstPage ? continuation : []
                    };
                }
            };
            var table = CreateTable(client);

            var result = await table.ReadAllAsync(TestContext.Current.CancellationToken);

            Assert.Equal(4, reads);
            Assert.Equal(4, queries);
            Assert.Equal(8, result.Version.Version);
            Assert.Equal("8", result.Version.VersionEtag);
            Assert.Equal("current", Assert.Single(result.Members).Item1.HostName);
        }

        [Fact]
        public async Task ReadAllCancellationStopsInconsistentSnapshotRetry()
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var reads = 0;
            var queries = 0;
            using var client = new RequestClient
            {
                Token = cancellation.Token,
                Read = _ =>
                {
                    reads++;
                    if (reads == 2)
                    {
                        cancellation.Cancel();
                    }
                    return new GetItemResponse { Item = CreateVersion(reads == 1 ? 7 : 8).GetFields(true) };
                },
                Query = _ =>
                {
                    queries++;
                    return new QueryResponse { Items = [CreateVersion(7).GetFields(true)], LastEvaluatedKey = [] };
                }
            };

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => CreateTable(client).ReadAllAsync(cancellation.Token));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Equal(2, reads);
            Assert.Equal(1, queries);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ReadRowRequiresMembershipHistory(bool versionExists)
        {
            using var client = new RequestClient
            {
                ReadTransaction = request =>
                {
                    Assert.Equal(2, request.TransactItems.Count);
                    Assert.Equal("127.0.0.1-11111-1", request.TransactItems[0].Get.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S);
                    Assert.Equal("VersionRow", request.TransactItems[1].Get.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S);
                    Assert.All(request.TransactItems, item =>
                    {
                        Assert.Equal("membership", item.Get.TableName);
                        Assert.Equal("cluster", item.Get.Key[SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME].S);
                    });
                    return new TransactGetItemsResponse
                    {
                        Responses = [new ItemResponse(), versionExists ? new ItemResponse { Item = CreateVersion(7).GetFields(true) } : new ItemResponse()]
                    };
                }
            };
            var table = CreateTable(client);
            var pending = table.ReadRowAsync(SiloAddress.New(IPAddress.Loopback, 11111, 1), TestContext.Current.CancellationToken);

            if (versionExists)
            {
                var result = await pending;
                Assert.Empty(result.Members);
                Assert.Equal(7, result.Version.Version);
                Assert.Equal("7", result.Version.VersionEtag);
            }
            else
            {
                await Assert.ThrowsAsync<KeyNotFoundException>(() => pending);
            }
        }

        [Fact]
        public async Task ReadAllPropagatesMissingHistoryAndMalformedRows()
        {
            using var missing = new RequestClient { Read = _ => new GetItemResponse() };
            await Assert.ThrowsAsync<KeyNotFoundException>(() => CreateTable(missing).ReadAllAsync(TestContext.Current.CancellationToken));
            var malformed = CreateRecord();
            malformed.Address = "invalid-address";
            using var corrupt = new RequestClient
            {
                Read = _ => new GetItemResponse { Item = CreateVersion(7).GetFields(true) },
                Query = _ => new QueryResponse { Items = [CreateVersion(7).GetFields(true), malformed.GetFields(true)], LastEvaluatedKey = [] }
            };

            await Assert.ThrowsAsync<FormatException>(() => CreateTable(corrupt).ReadAllAsync(TestContext.Current.CancellationToken));
        }

        public static IEnumerable<object[]> MalformedVersionRows()
        {
            foreach (var operation in new[] { "ReadRow", "ReadAllBefore", "ReadAllQuery", "ReadAllAfter", "HeartbeatHistory" })
            {
                foreach (var attribute in new[] { SiloInstanceRecord.MEMBERSHIP_VERSION_PROPERTY_NAME, SiloInstanceRecord.ETAG_PROPERTY_NAME })
                {
                    foreach (var corruption in new[] { "missing", "string", "null", "fractional", "overflow" })
                    {
                        yield return [operation, attribute, corruption];
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(MalformedVersionRows))]
        public async Task MembershipReadsRejectMalformedVersionAttributes(string operation, string attribute, string corruption)
        {
            var fields = CreateVersion(7).GetFields(includeKeys: true);
            if (corruption == "missing")
            {
                fields.Remove(attribute);
            }
            else
            {
                fields[attribute] = corruption switch
                {
                    "string" => new AttributeValue("not-a-number"),
                    "null" => new AttributeValue { NULL = true },
                    "fractional" => new AttributeValue { N = "1.5" },
                    "overflow" => new AttributeValue { N = "2147483648" },
                    _ => throw new ArgumentOutOfRangeException(nameof(corruption))
                };
            }

            var reads = 0;
            using var client = new RequestClient
            {
                Read = request =>
                {
                    if (request.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S != SiloInstanceRecord.TABLE_VERSION_ROW)
                    {
                        return new GetItemResponse();
                    }

                    reads++;
                    var malformed = operation is "ReadAllBefore" or "HeartbeatHistory"
                        || (operation == "ReadAllAfter" && reads == 2);
                    return new GetItemResponse { Item = malformed ? fields : CreateVersion(7).GetFields(true) };
                },
                Query = _ => new QueryResponse
                {
                    Items = [operation == "ReadAllQuery" ? fields : CreateVersion(7).GetFields(true)],
                    LastEvaluatedKey = []
                },
                ReadTransaction = _ => new TransactGetItemsResponse
                {
                    Responses = [new ItemResponse(), new ItemResponse { Item = fields }]
                },
                Update = _ => throw new ConditionalCheckFailedException("Silo row absent.")
            };
            var table = CreateTable(client);
            var address = SiloAddress.New(IPAddress.Loopback, 11111, 1);

            var exception = await Assert.ThrowsAsync<FormatException>(() => operation switch
            {
                "ReadRow" => table.ReadRowAsync(address, TestContext.Current.CancellationToken),
                "HeartbeatHistory" => table.UpdateIAmAliveAsync(new MembershipEntry
                {
                    SiloAddress = address,
                    IAmAliveTime = DateTime.UnixEpoch
                }, TestContext.Current.CancellationToken),
                _ => table.ReadAllAsync(TestContext.Current.CancellationToken)
            });

            Assert.Contains("Membership table version row", exception.Message);
            Assert.Contains(attribute, exception.Message);
            Assert.Equal(0, client.WriteCount);
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, int.MaxValue)]
        [InlineData(true, int.MaxValue)]
        public async Task MembershipReadsAcceptValidVersionAttributes(bool readAll, int version)
        {
            var fields = CreateVersion(version).GetFields(includeKeys: true);
            using var client = new RequestClient
            {
                Read = _ => new GetItemResponse { Item = fields },
                Query = _ => new QueryResponse { Items = [fields], LastEvaluatedKey = [] },
                ReadTransaction = _ => new TransactGetItemsResponse
                {
                    Responses = [new ItemResponse(), new ItemResponse { Item = fields }]
                }
            };
            var table = CreateTable(client);

            var result = readAll
                ? await table.ReadAllAsync(TestContext.Current.CancellationToken)
                : await table.ReadRowAsync(SiloAddress.New(IPAddress.Loopback, 11111, 1), TestContext.Current.CancellationToken);

            Assert.Equal(version, result.Version.Version);
            Assert.Equal(version.ToString(CultureInfo.InvariantCulture), result.Version.VersionEtag);
            Assert.Empty(result.Members);
        }

        private static SiloInstanceRecord CreateRecord() => new()
        {
            DeploymentId = "cluster",
            SiloIdentity = "127.0.0.1-11111-1",
            Address = "127.0.0.1",
            Port = 11111,
            Generation = 1,
            HostName = "host",
            Status = (int)SiloStatus.Active,
            StartTime = "2025-12-31 00:00:00.000 GMT",
            IAmAliveTime = "2026-01-01 00:00:00.000 GMT",
            MembershipVersion = 7,
            ETag = 3
        };

        private static SiloInstanceRecord CreateVersion(int version) => new()
        {
            DeploymentId = "cluster",
            SiloIdentity = SiloInstanceRecord.TABLE_VERSION_ROW,
            MembershipVersion = version,
            ETag = version
        };

        private static Exception CreateStorageFailure(string kind) => kind switch
        {
            "resource" => new ResourceNotFoundException("Missing membership table."),
            "authorization" => new AmazonDynamoDBException("Access denied.") { ErrorCode = "AccessDeniedException" },
            "network" => new HttpRequestException("Connection failed."),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        private static DynamoDBMembershipTable CreateTable(AmazonDynamoDBClient client)
        {
            var table = new DynamoDBMembershipTable(
                NullLoggerFactory.Instance,
                Options.Create(new DynamoDBClusteringOptions { TableName = "membership" }),
                Options.Create(new ClusterOptions { ClusterId = "cluster" }));
            var storage = new DynamoDBStorage(NullLogger<DynamoDBStorage>.Instance, "http://localhost");
            var clientField = typeof(DynamoDBStorage).GetField("_ddbClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.IsAssignableFrom<IDisposable>(clientField.GetValue(storage)).Dispose();
            clientField.SetValue(storage, client);
            typeof(DynamoDBMembershipTable).GetField("storage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(table, storage);
            return table;
        }

        private sealed class HeartbeatClient() : AmazonDynamoDBClient(
            new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { ServiceURL = "http://localhost" })
        {
            public Exception? Failure { get; init; }
            public Exception? ReadFailure { get; init; }
            public SiloInstanceRecord? Current { get; init; }
            public bool VersionExists { get; init; } = true;
            public List<UpdateItemRequest> Updates { get; } = [];
            public List<GetItemRequest> Reads { get; } = [];
            public SiloInstanceRecord Version { get; } = new()
            {
                DeploymentId = "cluster",
                SiloIdentity = SiloInstanceRecord.TABLE_VERSION_ROW,
                MembershipVersion = 7,
                ETag = 7
            };

            public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request, CancellationToken cancellationToken = default)
            {
                Assert.Equal(TestContext.Current.CancellationToken, cancellationToken);
                Updates.Add(request);
                Assert.Equal("membership", request.TableName);
                Assert.Equal("attribute_exists(DeploymentId) AND attribute_exists(SiloIdentity)"
                    + " AND (attribute_not_exists(IAmAliveTime) OR IAmAliveTime < :IAmAliveTime)", request.ConditionExpression);
                Assert.Equal("SET IAmAliveTime = :IAmAliveTime", request.UpdateExpression);
                Assert.Equal(ReturnValue.UPDATED_NEW, request.ReturnValues);
                var heartbeat = Assert.Single(request.ExpressionAttributeValues).Value;
                if (Failure is not null)
                {
                    return Task.FromException<UpdateItemResponse>(Failure);
                }
                if (Current is null || string.CompareOrdinal(Current.IAmAliveTime, heartbeat.S) >= 0)
                {
                    return Task.FromException<UpdateItemResponse>(new ConditionalCheckFailedException("Heartbeat condition failed."));
                }
                Current.IAmAliveTime = heartbeat.S;
                return Task.FromResult(new UpdateItemResponse
                {
                    Attributes = new() { [SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME] = heartbeat }
                });
            }

            public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
            {
                Assert.Equal(TestContext.Current.CancellationToken, cancellationToken);
                Assert.True(request.ConsistentRead);
                Reads.Add(request);
                if (ReadFailure is not null)
                {
                    return Task.FromException<GetItemResponse>(ReadFailure);
                }
                if (Current is not null && request.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S == Current.SiloIdentity)
                {
                    return Task.FromResult(new GetItemResponse { Item = Current.GetFields(includeKeys: true) });
                }
                return Task.FromResult(VersionExists && request.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S == SiloInstanceRecord.TABLE_VERSION_ROW
                    ? new GetItemResponse { Item = Version.GetFields(includeKeys: true) }
                    : new GetItemResponse());
            }
        }

        private sealed class RequestClient() : AmazonDynamoDBClient(
            new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { ServiceURL = "http://localhost" })
        {
            public CancellationToken Token { get; init; } = TestContext.Current.CancellationToken;
            public Func<GetItemRequest, GetItemResponse> Read { get; init; } = _ => throw new InvalidOperationException("Unexpected read.");
            public Func<QueryRequest, QueryResponse> Query { get; init; } = _ => throw new InvalidOperationException("Unexpected query.");
            public Func<TransactWriteItemsRequest, TransactWriteItemsResponse> Write { get; init; } = _ => throw new InvalidOperationException("Unexpected write.");
            public Func<TransactGetItemsRequest, TransactGetItemsResponse> ReadTransaction { get; init; } = _ => throw new InvalidOperationException("Unexpected transactional read.");
            public Func<UpdateItemRequest, UpdateItemResponse> Update { get; init; } = _ => throw new InvalidOperationException("Unexpected update.");
            public int ReadCount { get; private set; }
            public int WriteCount { get; private set; }
            public int DeleteCount { get; private set; }

            public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
            {
                Assert.Equal(Token, cancellationToken);
                ReadCount++;
                return Task.FromResult(Read(request));
            }

            public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            {
                Assert.Equal(Token, cancellationToken);
                return Task.FromResult(Query(request));
            }

            public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken cancellationToken = default)
            {
                Assert.Equal(Token, cancellationToken);
                WriteCount++;
                return Task.FromResult(Write(request));
            }

            public override Task<TransactGetItemsResponse> TransactGetItemsAsync(TransactGetItemsRequest request, CancellationToken cancellationToken = default)
            {
                Assert.Equal(Token, cancellationToken);
                return Task.FromResult(ReadTransaction(request));
            }

            public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request, CancellationToken cancellationToken = default)
            {
                Assert.Equal(Token, cancellationToken);
                return Task.FromResult(Update(request));
            }

            public override Task<DeleteItemResponse> DeleteItemAsync(DeleteItemRequest request, CancellationToken cancellationToken = default)
            {
                Assert.Equal(Token, cancellationToken);
                DeleteCount++;
                throw new InvalidOperationException("Unexpected delete.");
            }
        }

        private sealed class BatchDeleteClient() : AmazonDynamoDBClient(
            new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { ServiceURL = "http://localhost" })
        {
            private readonly List<TaskCompletionSource<BatchWriteItemResponse>> _completions = [];
            private readonly List<(DeleteItemRequest Request, TaskCompletionSource<DeleteItemResponse> Completion)> _deleteCompletions = [];
            private Dictionary<string, SiloInstanceRecord>? _records;
            private bool _released;

            public Action? OnFirstDeleteRequest { get; set; }
            public Action<int>? OnDeleteRequest { get; init; }
            public Exception? DeleteFailure { get; set; }
            public int RowCount { get; init; } = 51;
            public int Version { get; } = 7;
            public int VersionEtag { get; } = 7;
            public int QueryCount { get; private set; }
            public Dictionary<string, SiloInstanceRecord> Records => _records ??= Enumerable.Range(0, RowCount).Select(i => new SiloInstanceRecord
            {
                DeploymentId = "cluster",
                SiloIdentity = $"silo-{i}",
                Status = (int)SiloStatus.Dead,
                IAmAliveTime = "2026-01-01 00:00:00.000 GMT",
                ETag = i + 1
            }).ToDictionary(record => record.SiloIdentity);
            public List<BatchWriteItemRequest> Requests { get; } = [];
            public List<DeleteItemRequest> Deletes { get; } = [];
            public List<CancellationToken> Tokens { get; } = [];
            public int PendingDeletes => _deleteCompletions.Count;
            public int PeakPendingDeletes { get; private set; }

            public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Equal("membership", request.TableName);
                Assert.True(request.ConsistentRead);
                Assert.Equal("DeploymentId = :DeploymentId", request.KeyConditionExpression);
                Assert.Equal("cluster", request.ExpressionAttributeValues[":DeploymentId"].S);
                QueryCount++;
                var version = new SiloInstanceRecord
                {
                    DeploymentId = "cluster",
                    SiloIdentity = SiloInstanceRecord.TABLE_VERSION_ROW,
                    MembershipVersion = Version,
                    ETag = VersionEtag
                };
                return Task.FromResult(new QueryResponse
                {
                    Items = Records.Values.Append(version).Select(record => record.GetFields(includeKeys: true)).ToList(),
                    LastEvaluatedKey = [],
                });
            }

            public override Task<BatchWriteItemResponse> BatchWriteItemAsync(BatchWriteItemRequest request, CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                Tokens.Add(cancellationToken);
                var completion = new TaskCompletionSource<BatchWriteItemResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                _completions.Add(completion);
                if (_released)
                {
                    completion.SetResult(new BatchWriteItemResponse { UnprocessedItems = [] });
                }

                if (Tokens.Count == 1)
                {
                    OnFirstDeleteRequest?.Invoke();
                }

                return completion.Task;
            }

            public override Task<DeleteItemResponse> DeleteItemAsync(
                DeleteItemRequest request, CancellationToken cancellationToken = default)
            {
                Deletes.Add(request);
                Tokens.Add(cancellationToken);
                if (Tokens.Count == 1)
                {
                    OnFirstDeleteRequest?.Invoke();
                }

                if (DeleteFailure is { } failure)
                {
                    DeleteFailure = null;
                    return Task.FromException<DeleteItemResponse>(failure);
                }

                var completion = new TaskCompletionSource<DeleteItemResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_released)
                {
                    CompleteDelete(request, completion);
                }
                else
                {
                    _deleteCompletions.Add((request, completion));
                    PeakPendingDeletes = Math.Max(PeakPendingDeletes, _deleteCompletions.Count);
                }

                OnDeleteRequest?.Invoke(Deletes.Count);
                return completion.Task;
            }

            public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(
                TransactWriteItemsRequest request, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("Unexpected cleanup transaction.");

            public void CompleteAll()
            {
                _released = true;
                foreach (var completion in _completions.ToArray())
                {
                    completion.TrySetResult(new BatchWriteItemResponse { UnprocessedItems = [] });
                }

                CompletePendingDeletes();
            }

            public void CompletePendingDeletes()
            {
                var pending = _deleteCompletions.ToArray();
                _deleteCompletions.Clear();
                foreach (var (request, completion) in pending)
                {
                    CompleteDelete(request, completion);
                }
            }

            private void CompleteDelete(DeleteItemRequest request, TaskCompletionSource<DeleteItemResponse> completion)
            {
                var identity = request.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S;
                Assert.NotEqual(SiloInstanceRecord.TABLE_VERSION_ROW, identity);
                var fields = Records.TryGetValue(identity, out var record) ? record.GetFields(includeKeys: true) : [];
                foreach (var condition in request.ConditionExpression.Split(" AND "))
                {
                    bool matches;
                    if (condition.StartsWith("attribute_not_exists(", StringComparison.Ordinal))
                    {
                        matches = !fields.ContainsKey(condition["attribute_not_exists(".Length..^1]);
                    }
                    else
                    {
                        var parts = condition.Split(" = ");
                        var expected = request.ExpressionAttributeValues[parts[1]];
                        matches = fields.TryGetValue(parts[0], out var current)
                            && expected.N == current.N && expected.S == current.S;
                    }

                    if (!matches)
                    {
                        completion.SetException(new ConditionalCheckFailedException("Row changed."));
                        return;
                    }
                }

                Records.Remove(identity);
                completion.SetResult(new DeleteItemResponse());
            }
        }

        [Fact]
        public void SiloIsDefunct_ParsesPersistedTimestampUsingInvariantCulture()
        {
            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
                var record = new SiloInstanceRecord
                {
                    IAmAliveTime = "2026-09-03 20:00:00.000 GMT",
                    Status = (int)SiloStatus.Dead
                };
                var cutoff = new DateTimeOffset(2026, 9, 3, 21, 0, 0, TimeSpan.Zero);

                Assert.True(DynamoDBMembershipTable.SiloIsDefunct(record, cutoff));
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }
    }
}
