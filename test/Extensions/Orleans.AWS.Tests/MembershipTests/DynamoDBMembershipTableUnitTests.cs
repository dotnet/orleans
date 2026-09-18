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
                            "SiloStatus = :SiloStatus AND ETag = :ETag AND IAmAliveTime = :IAmAliveTime",
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
                if (field != nameof(SiloInstanceRecord.IAmAliveTime))
                {
                    row.ETag++;
                    row.MembershipVersion++;
                }
                switch (field)
                {
                    case nameof(SiloInstanceRecord.IAmAliveTime): row.IAmAliveTime = recent; break;
                    case nameof(SiloInstanceRecord.StartTime): row.StartTime = recent; break;
                    case nameof(SiloInstanceRecord.SuspectingTimes):
                        row.SuspectingSilos = "127.0.0.1:11112@1";
                        row.SuspectingTimes = recent;
                        break;
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

        [Fact]
        public async Task CleanupPreservesVersionRowWithStrayMembershipFields()
        {
            using var client = new BatchDeleteClient
            {
                RowCount = 1,
                CustomizeVersion = version =>
                {
                    version.Status = (int)SiloStatus.Dead;
                    version.StartTime = "2026-01-01 00:00:00.000 GMT";
                    version.IAmAliveTime = "2026-01-01 00:00:00.000 GMT";
                }
            };
            client.CompleteAll();

            await DeleteEntries(CreateTable(client), cleanup: true, TestContext.Current.CancellationToken);

            Assert.Equal("silo-0", Assert.Single(client.Deletes).Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S);
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

        [Theory]
        [InlineData("valid")]
        [InlineData("absent")]
        [InlineData("number")]
        [InlineData("null")]
        public async Task HeartbeatUsesOneUnconditionalTimestampWrite(string previousValue)
        {
            var fields = CreateRecord().GetFields(includeKeys: true);
            switch (previousValue)
            {
                case "absent": fields.Remove(SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME); break;
                case "number": fields[SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME] = new AttributeValue { N = "123" }; break;
                case "null": fields[SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME] = new AttributeValue { NULL = true }; break;
            }
            var before = new Dictionary<string, AttributeValue>(fields);
            var updates = 0;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            using var client = new RequestClient
            {
                Token = cancellation.Token,
                Update = request =>
                {
                    Assert.Equal(1, ++updates);
                    Assert.Equal("membership", request.TableName);
                    Assert.Equal(2, request.Key.Count);
                    Assert.Equal("cluster", request.Key[SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME].S);
                    Assert.Equal("127.0.0.1-11111-1", request.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S);
                    Assert.Null(request.ConditionExpression);
                    Assert.Equal("SET IAmAliveTime = :IAmAliveTime", request.UpdateExpression);
                    Assert.Equal(ReturnValue.UPDATED_NEW, request.ReturnValues);
                    var value = Assert.Single(request.ExpressionAttributeValues);
                    Assert.Equal(":IAmAliveTime", value.Key);
                    Assert.Equal("2026-01-02 00:00:00.000 GMT", value.Value.S);
                    fields[SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME] = value.Value;
                    return new UpdateItemResponse { Attributes = new() { [SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME] = value.Value } };
                }
            };

            await CreateTable(client).UpdateIAmAliveAsync(new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
            }, cancellation.Token);

            Assert.Equal(1, updates);
            Assert.Equal(0, client.ReadCount);
            Assert.Equal(0, client.WriteCount);
            Assert.Equal(0, client.DeleteCount);
            Assert.Equal("2026-01-02 00:00:00.000 GMT", fields[SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME].S);
            Assert.Equal(before.Keys.Append(SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME).Distinct().Order(), fields.Keys.Order());
            foreach (var field in before.Where(field => field.Key != SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME))
            {
                Assert.Same(field.Value, fields[field.Key]);
            }
        }

        [Theory]
        [InlineData("resource")]
        [InlineData("authorization")]
        [InlineData("network")]
        [InlineData("cancellation")]
        public async Task HeartbeatPropagatesNativeFailures(string kind)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var failure = kind == "cancellation"
                ? new OperationCanceledException(cancellation.Token)
                : CreateStorageFailure(kind);
            var updates = 0;
            using var client = new RequestClient
            {
                Token = cancellation.Token,
                Update = request =>
                {
                    Assert.Equal(1, ++updates);
                    Assert.Null(request.ConditionExpression);
                    throw failure;
                }
            };

            var exception = await Assert.ThrowsAnyAsync<Exception>(() => CreateTable(client).UpdateIAmAliveAsync(new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = DateTime.UnixEpoch
            }, cancellation.Token));

            Assert.Same(failure, exception);
            Assert.Equal(1, updates);
            Assert.Equal(0, client.ReadCount);
            Assert.Equal(0, client.WriteCount);
        }

        public static IEnumerable<object[]> MalformedRecencyAttributes()
        {
            foreach (var operation in new[] { "ReadRow", "ReadAll", "Cleanup" })
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
                Query = _ => new QueryResponse { Items = [CreateVersion(7).GetFields(true), fields], LastEvaluatedKey = [] }
            };
            var table = CreateTable(client);
            var entry = new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = DateTime.UnixEpoch
            };

            var exception = await Assert.ThrowsAsync<FormatException>(() => operation switch
            {
                "ReadRow" => table.ReadRowAsync(entry.SiloAddress, TestContext.Current.CancellationToken),
                "ReadAll" => table.ReadAllAsync(TestContext.Current.CancellationToken),
                "Cleanup" => table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.MaxValue, TestContext.Current.CancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            });

            Assert.Contains(attribute, exception.Message);
            Assert.Contains(row.SiloIdentity, exception.Message);
            Assert.Equal(0, client.WriteCount);
            Assert.Equal(0, client.DeleteCount);
            Assert.Same(invalid, fields[attribute]);
        }

        [Theory]
        [InlineData("ReadRow")]
        [InlineData("ReadAll")]
        [InlineData("Cleanup")]
        public async Task MembershipOperationsPreserveAbsentLegacyRecencyAttributes(string operation)
        {
            var row = CreateRecord();
            row.Status = (int)SiloStatus.Dead;
            row.IAmAliveTime = null;
            row.StartTime = null;
            var fields = row.GetFields(includeKeys: true);
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
                Query = _ => new QueryResponse { Items = [CreateVersion(7).GetFields(true), fields], LastEvaluatedKey = [] }
            };
            var table = CreateTable(client);
            var entry = new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
                IAmAliveTime = DateTime.UnixEpoch
            };

            switch (operation)
            {
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
            }

            Assert.Equal(0, client.WriteCount);
            Assert.Equal(0, client.DeleteCount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MembershipWritesAtomicallyConditionRowAndTableVersion(bool update)
        {
            var current = CreateRecord();
            current.SuspectingSilos = "127.0.0.1:11112@1";
            current.SuspectingTimes = current.IAmAliveTime;
            using var client = new RequestClient
            {
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
                    Assert.Equal("2026-01-01 00:00:01.000 GMT", put.Item[SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME].S);
                    Assert.False(put.Item.ContainsKey(SiloInstanceRecord.SUSPECTING_SILOS_PROPERTY_NAME));
                    Assert.False(put.Item.ContainsKey(SiloInstanceRecord.SUSPECTING_TIMES_PROPERTY_NAME));
                    if (update)
                    {
                        Assert.Equal("ETag = :currentETag", put.ConditionExpression);
                        Assert.Single(put.ExpressionAttributeValues);
                        Assert.Equal("3", put.ExpressionAttributeValues[":currentETag"].N);
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
            Assert.Equal(0, client.ReadCount);
        }

        [Theory]
        [InlineData("missing-row")]
        [InlineData("stale-row")]
        [InlineData("stale-table")]
        public async Task UpdateRejectsCanonicalConflictsAtomically(string conflict)
        {
            var row = conflict == "missing-row" ? null : CreateRecord().GetFields(true);
            var version = CreateVersion(7).GetFields(true);
            using var client = new RequestClient
            {
                Write = request =>
                {
                    Assert.Equal(2, request.TransactItems.Count);
                    var put = Assert.IsType<Put>(request.TransactItems[0].Put);
                    var update = Assert.IsType<Update>(request.TransactItems[1].Update);
                    Assert.Equal("ETag = :currentETag", put.ConditionExpression);
                    Assert.Equal("ETag = :currentETag", update.ConditionExpression);
                    var rowMatches = row is not null && row[SiloInstanceRecord.ETAG_PROPERTY_NAME].N == put.ExpressionAttributeValues[":currentETag"].N;
                    var versionMatches = version[SiloInstanceRecord.ETAG_PROPERTY_NAME].N == update.ExpressionAttributeValues[":currentETag"].N;
                    Assert.False(rowMatches && versionMatches);
                    throw new TransactionCanceledException("Canonical condition failed.")
                    {
                        CancellationReasons =
                        [
                            new() { Code = rowMatches ? "None" : "ConditionalCheckFailed" },
                            new() { Code = versionMatches ? "None" : "ConditionalCheckFailed" }
                        ]
                    };
                }
            };
            var table = CreateTable(client);

            Assert.False(await table.UpdateRowAsync(new MembershipEntry
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1)
            }, conflict == "stale-row" ? "2" : "3", new TableVersion(8, conflict == "stale-table" ? "6" : "7"),
                TestContext.Current.CancellationToken));
            Assert.Equal(0, client.ReadCount);
            Assert.Equal(1, client.WriteCount);
            Assert.Equal("7", version[SiloInstanceRecord.MEMBERSHIP_VERSION_PROPERTY_NAME].N);
            Assert.Equal("7", version[SiloInstanceRecord.ETAG_PROPERTY_NAME].N);
            if (row is not null)
            {
                Assert.Equal("3", row[SiloInstanceRecord.ETAG_PROPERTY_NAME].N);
                Assert.Equal("7", row[SiloInstanceRecord.MEMBERSHIP_VERSION_PROPERTY_NAME].N);
            }
        }

        [Fact]
        public async Task OwnerHeartbeatPreservesCanonicalTokensForFullRowReplacement()
        {
            var row = CreateRecord().GetFields(true);
            var version = CreateVersion(7).GetFields(true);
            var reads = 0;
            var heartbeats = 0;
            using var client = new RequestClient
            {
                ReadTransaction = _ =>
                {
                    reads++;
                    return new TransactGetItemsResponse
                    {
                        Responses = [new ItemResponse { Item = row }, new ItemResponse { Item = version }]
                    };
                },
                Update = request =>
                {
                    heartbeats++;
                    Assert.Null(request.ConditionExpression);
                    Assert.Equal("SET IAmAliveTime = :IAmAliveTime", request.UpdateExpression);
                    var heartbeat = Assert.Single(request.ExpressionAttributeValues).Value;
                    row[SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME] = heartbeat;
                    return new UpdateItemResponse { Attributes = new() { [SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME] = heartbeat } };
                },
                Write = request =>
                {
                    Assert.Equal(2, request.TransactItems.Count);
                    var put = Assert.IsType<Put>(request.TransactItems[0].Put);
                    var update = Assert.IsType<Update>(request.TransactItems[1].Update);
                    Assert.Equal("ETag = :currentETag", put.ConditionExpression);
                    Assert.Single(put.ExpressionAttributeValues);
                    Assert.Equal(row[SiloInstanceRecord.ETAG_PROPERTY_NAME].N, put.ExpressionAttributeValues[":currentETag"].N);
                    Assert.Equal("ETag = :currentETag", update.ConditionExpression);
                    Assert.Equal(version[SiloInstanceRecord.ETAG_PROPERTY_NAME].N, update.ExpressionAttributeValues[":currentETag"].N);
                    row = put.Item;
                    foreach (var attribute in new[] { SiloInstanceRecord.MEMBERSHIP_VERSION_PROPERTY_NAME, SiloInstanceRecord.ETAG_PROPERTY_NAME })
                    {
                        version[attribute] = update.ExpressionAttributeValues[$":{attribute}"];
                    }
                    return new TransactWriteItemsResponse();
                }
            };
            var table = CreateTable(client);
            var address = SiloAddress.New(IPAddress.Loopback, 11111, 1);
            var snapshot = await table.ReadRowAsync(address, TestContext.Current.CancellationToken);
            var (entry, etag) = Assert.Single(snapshot.Members);

            await table.UpdateIAmAliveAsync(new MembershipEntry
            {
                SiloAddress = address,
                IAmAliveTime = entry.IAmAliveTime.AddSeconds(10)
            }, TestContext.Current.CancellationToken);

            Assert.Equal("2026-01-01 00:00:10.000 GMT", row[SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME].S);
            Assert.Equal(etag, row[SiloInstanceRecord.ETAG_PROPERTY_NAME].N);
            Assert.Equal(snapshot.Version.VersionEtag, version[SiloInstanceRecord.ETAG_PROPERTY_NAME].N);
            entry.Status = SiloStatus.ShuttingDown;
            Assert.True(await table.UpdateRowAsync(entry, etag, snapshot.Version.Next(), TestContext.Current.CancellationToken));
            Assert.Equal(((int)SiloStatus.ShuttingDown).ToString(CultureInfo.InvariantCulture), row[SiloInstanceRecord.STATUS_PROPERTY_NAME].N);
            Assert.Equal("4", row[SiloInstanceRecord.ETAG_PROPERTY_NAME].N);
            Assert.Equal("8", version[SiloInstanceRecord.ETAG_PROPERTY_NAME].N);
            Assert.Equal("8", version[SiloInstanceRecord.MEMBERSHIP_VERSION_PROPERTY_NAME].N);
            Assert.Equal(1, reads);
            Assert.Equal(1, heartbeats);
            Assert.Equal(1, client.WriteCount);
            Assert.Equal(0, client.ReadCount);
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ReadAllRetriesWhenCanonicalMutationCrossesPages(bool versionFirst)
        {
            var reads = 0;
            var queries = 0;
            var currentVersion = 7;
            var member = CreateRecord();
            if (versionFirst)
            {
                member.Address = "aaaa::1";
                member.SiloIdentity = SiloInstanceRecord.ConstructSiloIdentity(SiloAddress.New(IPAddress.Parse(member.Address), 11111, 1));
            }
            var continuation = versionFirst ? CreateVersion(7).GetKeys() : member.GetKeys();
            using var client = new RequestClient
            {
                Read = request =>
                {
                    Assert.True(++reads <= 2);
                    Assert.True(request.ConsistentRead);
                    Assert.Equal("VersionRow", request.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S);
                    return new GetItemResponse { Item = CreateVersion(currentVersion).GetFields(true) };
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
                    member.MembershipVersion = currentVersion;
                    member.ETag = currentVersion - 4;
                    member.HostName = $"host-{currentVersion}";
                    var response = new QueryResponse
                    {
                        Items = [firstPage == versionFirst ? CreateVersion(currentVersion).GetFields(true) : member.GetFields(true)],
                        LastEvaluatedKey = firstPage ? continuation : []
                    };
                    currentVersion = 8;
                    return response;
                }
            };
            var table = CreateTable(client);

            var result = await table.ReadAllAsync(TestContext.Current.CancellationToken);

            Assert.Equal(2, reads);
            Assert.Equal(4, queries);
            Assert.Equal(8, result.Version.Version);
            Assert.Equal("8", result.Version.VersionEtag);
            Assert.Equal("host-8", Assert.Single(result.Members).Item1.HostName);
        }

        [Fact]
        public async Task ReadAllReturnsCompleteViewWhenMutationFollowsQuery()
        {
            var reads = 0;
            var queries = 0;
            var currentVersion = 7;
            using var client = new RequestClient
            {
                Read = request =>
                {
                    Assert.Equal(1, ++reads);
                    Assert.True(request.ConsistentRead);
                    return new GetItemResponse { Item = CreateVersion(currentVersion).GetFields(true) };
                },
                Query = request =>
                {
                    Assert.Equal(1, ++queries);
                    Assert.True(request.ConsistentRead);
                    var response = new QueryResponse { Items = [CreateRecord().GetFields(true), CreateVersion(7).GetFields(true)], LastEvaluatedKey = [] };
                    currentVersion = 8;
                    return response;
                }
            };

            var result = await CreateTable(client).ReadAllAsync(TestContext.Current.CancellationToken);

            Assert.Equal(7, result.Version.Version);
            Assert.Equal("7", result.Version.VersionEtag);
            Assert.Equal("host", Assert.Single(result.Members).Item1.HostName);
            Assert.Equal(1, reads);
            Assert.Equal(1, queries);
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
                    return new GetItemResponse { Item = CreateVersion(7).GetFields(true) };
                },
                Query = _ =>
                {
                    queries++;
                    cancellation.Cancel();
                    return new QueryResponse { Items = [CreateVersion(8).GetFields(true)], LastEvaluatedKey = [] };
                }
            };

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => CreateTable(client).ReadAllAsync(cancellation.Token));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Equal(1, reads);
            Assert.Equal(1, queries);
        }

        [Fact]
        public async Task ReadAllFailsWhenQueryOmitsVersionRow()
        {
            var reads = 0;
            var queries = 0;
            using var client = new RequestClient
            {
                Read = _ =>
                {
                    Assert.Equal(1, ++reads);
                    return new GetItemResponse { Item = CreateVersion(7).GetFields(true) };
                },
                Query = _ =>
                {
                    queries++;
                    return new QueryResponse { Items = [CreateRecord().GetFields(true)], LastEvaluatedKey = [] };
                }
            };

            var exception = await Assert.ThrowsAsync<KeyNotFoundException>(
                () => CreateTable(client).ReadAllAsync(TestContext.Current.CancellationToken));

            Assert.Equal("No version row found for membership table", exception.Message);
            Assert.Equal(1, reads);
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

        public static IEnumerable<object[]> MalformedMembershipTokens()
        {
            foreach (var operation in new[] { "ReadRow", "ReadRowMember", "ReadAllBefore", "ReadAllQuery", "ReadAllMember", "CleanupMember" })
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
        [MemberData(nameof(MalformedMembershipTokens))]
        public async Task MembershipOperationsRejectMalformedTokens(string operation, string attribute, string corruption)
        {
            var member = operation.EndsWith("Member", StringComparison.Ordinal);
            var fields = member ? CreateRecord().GetFields(true) : CreateVersion(7).GetFields(true);
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

            using var client = new RequestClient
            {
                Read = request =>
                {
                    if (request.Key[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S != SiloInstanceRecord.TABLE_VERSION_ROW)
                    {
                        return new GetItemResponse();
                    }

                    return new GetItemResponse { Item = operation == "ReadAllBefore" ? fields : CreateVersion(7).GetFields(true) };
                },
                Query = _ => new QueryResponse
                {
                    Items = member ? [fields, CreateVersion(7).GetFields(true)] : [fields],
                    LastEvaluatedKey = []
                },
                ReadTransaction = _ => new TransactGetItemsResponse
                {
                    Responses = member
                        ? [new ItemResponse { Item = fields }, new ItemResponse { Item = CreateVersion(7).GetFields(true) }]
                        : [new ItemResponse(), new ItemResponse { Item = fields }]
                }
            };
            var table = CreateTable(client);
            var address = SiloAddress.New(IPAddress.Loopback, 11111, 1);

            var exception = await Assert.ThrowsAsync<FormatException>(() => operation switch
            {
                "ReadRow" or "ReadRowMember" => table.ReadRowAsync(address, TestContext.Current.CancellationToken),
                "CleanupMember" => table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.MaxValue, TestContext.Current.CancellationToken),
                _ => table.ReadAllAsync(TestContext.Current.CancellationToken)
            });

            Assert.Contains(member ? "Membership row for silo" : "Membership table version row", exception.Message);
            Assert.Contains(attribute, exception.Message);
            Assert.Equal(0, client.WriteCount);
            Assert.Equal(0, client.DeleteCount);
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
            public Action<SiloInstanceRecord>? CustomizeVersion { get; init; }
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
                CustomizeVersion?.Invoke(version);
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
