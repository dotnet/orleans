using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using org.apache.zookeeper;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Membership;
using TestExtensions;
using Xunit;

namespace UnitTests.MembershipTests
{
    [TestCategory("Membership"), TestCategory("ZooKeeper")]
    [TestSuite("BVT")]
    [TestProvider("ZooKeeper")]
    [TestArea("Membership")]
    public sealed class ZooKeeperBasedMembershipTableUnitTests
    {
        [Fact]
        public void Constructor_NullLogger_ThrowsArgumentNullException()
        {
            var exception = Assert.Throws<ArgumentNullException>(() =>
                new ZooKeeperBasedMembershipTable(
                    null!,
                    CreateMembershipTableOptions(),
                    CreateClusterOptions()));

            Assert.Equal("logger", exception.ParamName);
        }

        [Fact]
        public void Constructor_NullMembershipTableOptions_ThrowsArgumentNullException()
        {
            var exception = Assert.Throws<ArgumentNullException>(() =>
                new ZooKeeperBasedMembershipTable(
                    NullLogger<ZooKeeperBasedMembershipTable>.Instance,
                    null!,
                    CreateClusterOptions()));

            Assert.Equal("membershipTableOptions", exception.ParamName);
        }

        [Fact]
        public void Constructor_NullClusterOptions_ThrowsArgumentNullException()
        {
            var exception = Assert.Throws<ArgumentNullException>(() =>
                new ZooKeeperBasedMembershipTable(
                    NullLogger<ZooKeeperBasedMembershipTable>.Instance,
                    CreateMembershipTableOptions(),
                    null!));

            Assert.Equal("clusterOptions", exception.ParamName);
        }

        [Fact]
        public void InsertRow_NullEntry_ThrowsArgumentNullException()
        {
            var sut = CreateSut();
            Task<bool>? returnedTask = null;

            var exception = Assert.Throws<ArgumentNullException>(() =>
            {
                returnedTask = sut.InsertRowAsync(null!, CreateTableVersion(), TestContext.Current.CancellationToken);
            });

            Assert.Equal("entry", exception.ParamName);
            Assert.Null(returnedTask);
        }

        [Fact]
        public void InsertRow_NullTableVersion_ThrowsArgumentNullException()
        {
            var sut = CreateSut();
            Task<bool>? returnedTask = null;

            var exception = Assert.Throws<ArgumentNullException>(() =>
            {
                returnedTask = sut.InsertRowAsync(CreateMembershipEntry(), null!, TestContext.Current.CancellationToken);
            });

            Assert.Equal("tableVersion", exception.ParamName);
            Assert.Null(returnedTask);
        }

        [Fact]
        public void InsertRow_NullEntryAndTableVersion_ThrowsForEntryFirst()
        {
            var sut = CreateSut();
            Task<bool>? returnedTask = null;

            var exception = Assert.Throws<ArgumentNullException>(() =>
            {
                returnedTask = sut.InsertRowAsync(null!, null!, TestContext.Current.CancellationToken);
            });

            Assert.Equal("entry", exception.ParamName);
            Assert.Null(returnedTask);
        }

        [Fact]
        public void UpdateRow_NullEntry_ThrowsArgumentNullException()
        {
            var sut = CreateSut();
            Task<bool>? returnedTask = null;

            var exception = Assert.Throws<ArgumentNullException>(() =>
            {
                returnedTask = sut.UpdateRowAsync(null!, "17", CreateTableVersion(), TestContext.Current.CancellationToken);
            });

            Assert.Equal("entry", exception.ParamName);
            Assert.Null(returnedTask);
        }

        [Fact]
        public void UpdateRow_NullTableVersion_ThrowsArgumentNullException()
        {
            var sut = CreateSut();
            Task<bool>? returnedTask = null;

            var exception = Assert.Throws<ArgumentNullException>(() =>
            {
                returnedTask = sut.UpdateRowAsync(CreateMembershipEntry(), "17", null!, TestContext.Current.CancellationToken);
            });

            Assert.Equal("tableVersion", exception.ParamName);
            Assert.Null(returnedTask);
        }

        [Fact]
        public void UpdateRow_NullEtag_ThrowsArgumentNullException()
        {
            var sut = CreateSut();
            Task<bool>? returnedTask = null;

            var exception = Assert.Throws<ArgumentNullException>(() =>
            {
                returnedTask = sut.UpdateRowAsync(CreateMembershipEntry(), null!, CreateTableVersion(), TestContext.Current.CancellationToken);
            });

            Assert.Equal("etag", exception.ParamName);
            Assert.Null(returnedTask);
        }

        [Fact]
        public void UpdateRow_NullEntryAndTableVersion_ThrowsForEntryFirst()
        {
            var sut = CreateSut();
            Task<bool>? returnedTask = null;

            var exception = Assert.Throws<ArgumentNullException>(() =>
            {
                returnedTask = sut.UpdateRowAsync(null!, "17", null!, TestContext.Current.CancellationToken);
            });

            Assert.Equal("entry", exception.ParamName);
            Assert.Null(returnedTask);
        }

        [Fact]
        public void UpdateIAmAlive_NullEntry_ThrowsArgumentNullException()
        {
            var sut = CreateSut();
            Task? returnedTask = null;

            var exception = Assert.Throws<ArgumentNullException>(() =>
            {
                returnedTask = sut.UpdateIAmAliveAsync(null!, TestContext.Current.CancellationToken);
            });

            Assert.Equal("entry", exception.ParamName);
            Assert.Null(returnedTask);
        }

        [Theory]
        [InlineData(nameof(IMembershipTable.InitializeMembershipTableAsync))]
        [InlineData(nameof(IMembershipTable.DeleteMembershipTableEntriesAsync))]
        [InlineData(nameof(IMembershipTable.CleanupDefunctSiloEntriesAsync))]
        [InlineData(nameof(IMembershipTable.ReadRowAsync))]
        [InlineData(nameof(IMembershipTable.ReadAllAsync))]
        [InlineData(nameof(IMembershipTable.InsertRowAsync))]
        [InlineData(nameof(IMembershipTable.UpdateRowAsync))]
        [InlineData(nameof(IMembershipTable.UpdateIAmAliveAsync))]
        public async Task MembershipOperations_PreCanceledToken_DoesNotCreateClient(string operation)
        {
            var sut = CreateSut("127.0.0.1:invalid-port");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.Cancel();
            var cancellationToken = cancellation.Token;
            Func<Task> invoke = operation switch
            {
                nameof(IMembershipTable.InitializeMembershipTableAsync) => () => sut.InitializeMembershipTableAsync(true, cancellationToken),
                nameof(IMembershipTable.DeleteMembershipTableEntriesAsync) => () => sut.DeleteMembershipTableEntriesAsync("cluster-a", cancellationToken),
                nameof(IMembershipTable.CleanupDefunctSiloEntriesAsync) => () => sut.CleanupDefunctSiloEntriesAsync(DateTimeOffset.UnixEpoch, cancellationToken),
                nameof(IMembershipTable.ReadRowAsync) => () => sut.ReadRowAsync(CreateSiloAddress(), cancellationToken),
                nameof(IMembershipTable.ReadAllAsync) => () => sut.ReadAllAsync(cancellationToken),
                nameof(IMembershipTable.InsertRowAsync) => () => sut.InsertRowAsync(CreateMembershipEntry(), CreateTableVersion(), cancellationToken),
                nameof(IMembershipTable.UpdateRowAsync) => () => sut.UpdateRowAsync(CreateMembershipEntry(), "17", CreateTableVersion(), cancellationToken),
                nameof(IMembershipTable.UpdateIAmAliveAsync) => () => sut.UpdateIAmAliveAsync(CreateMembershipEntry(), cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(invoke);

            Assert.Equal(cancellationToken, exception.CancellationToken);
        }

        [Theory]
        [InlineData("localhost:2181", "cluster-a", "localhost:2181", "/cluster-a", "localhost:2181/cluster-a")]
        [InlineData("localhost:2181/", "/cluster-a", "localhost:2181/", "//cluster-a", "localhost:2181///cluster-a")]
        public void Constructor_ValidOptions_PreservesLiteralConnectionAndClusterPathComposition(
            string connectionString,
            string clusterId,
            string expectedRootConnectionString,
            string expectedClusterPath,
            string expectedDeploymentConnectionString)
        {
            var sut = CreateSut(connectionString, clusterId);

            Assert.Equal(expectedRootConnectionString, GetPrivateField<string>(sut, "rootConnectionString"));
            Assert.Equal(expectedClusterPath, GetPrivateField<string>(sut, "clusterPath"));
            Assert.Equal(expectedDeploymentConnectionString, GetPrivateField<string>(sut, "deploymentConnectionString"));
        }

        [Fact]
        public void ConvertToRowPath_ValidAddress_PrefixesParsableAddressWithSlash()
        {
            var address = CreateSiloAddress();

            var result = InvokePrivatePathMethod("ConvertToRowPath", address);

            Assert.Equal("/127.0.0.1:11111@12345", result);
            Assert.EndsWith(address.ToParsableString(), result, StringComparison.Ordinal);
        }

        [Fact]
        public void ConvertToRowIAmAlivePath_ValidAddress_AppendsIAmAliveSegment()
        {
            var address = CreateSiloAddress();

            var result = InvokePrivatePathMethod("ConvertToRowIAmAlivePath", address);

            Assert.Equal("/127.0.0.1:11111@12345/IAmAlive", result);
            Assert.Equal(InvokePrivatePathMethod("ConvertToRowPath", address) + "/IAmAlive", result);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Read_ConcurrentStatusUpdate_RetriesWithCoherentVersion(bool pointRead)
        {
            var (fake, entry) = await CreateNativeTable();
            fake.AfterRead = async path =>
            {
                if (path == ZooKeeperNativeFake.RowPath(entry.SiloAddress))
                {
                    fake.AfterRead = null;
                    entry.Status = SiloStatus.Dead;
                    Assert.True(await ZooKeeperBasedMembershipTable.UpdateRowCoreAsync(
                        fake.Operations, entry, "0", new TableVersion(2, "1"), TestContext.Current.CancellationToken));
                }
            };

            var result = await Read(fake, pointRead ? entry.SiloAddress : null);

            var row = Assert.Single(result.Members);
            Assert.Equal(SiloStatus.Dead, row.Item1.Status);
            Assert.Equal("1", row.Item2);
            Assert.Equal(2, result.Version.Version);
            Assert.Equal("2", result.Version.VersionEtag);
            Assert.Equal(2, fake.Calls.Count(call => call == "sync /"));
            Assert.Equal("sync /", fake.Calls[0]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Read_ConcurrentUnversionedCleanup_RetriesChildVersion(bool pointRead)
        {
            var (fake, entry) = await CreateNativeTable(SiloStatus.Dead);
            fake.AfterRead = async path =>
            {
                if (path == ZooKeeperNativeFake.RowPath(entry.SiloAddress))
                {
                    fake.AfterRead = null;
                    await ZooKeeperBasedMembershipTable.CleanupCoreAsync(
                        fake.Operations, DateTime.UnixEpoch.AddDays(2), TestContext.Current.CancellationToken);
                }
            };

            var result = await Read(fake, pointRead ? entry.SiloAddress : null);

            Assert.Empty(result.Members);
            Assert.Equal(1, result.Version.Version);
            Assert.Equal("1", result.Version.VersionEtag);
            Assert.Single(fake.Nodes);
            Assert.Equal(2, fake.Nodes["/"].ChildrenVersion);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Read_CleanupAfterHeartbeatRead_FencesCompletedRows(bool pointRead)
        {
            var (fake, entry) = await CreateNativeTable(SiloStatus.Dead);
            fake.AfterRead = async path =>
            {
                if (path == ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress))
                {
                    fake.AfterRead = null;
                    await ZooKeeperBasedMembershipTable.CleanupCoreAsync(
                        fake.Operations, DateTime.UnixEpoch.AddDays(2), TestContext.Current.CancellationToken);
                }
            };

            var result = await Read(fake, pointRead ? entry.SiloAddress : null);

            Assert.Empty(result.Members);
            Assert.Equal(1, result.Version.Version);
            Assert.Single(fake.Nodes);
        }

        [Fact]
        public async Task Read_MissingPointRow_ReturnsEmptyViewWithCurrentVersion()
        {
            var fake = new ZooKeeperNativeFake();
            fake.Nodes["/"] = new([], 17);

            var result = await Read(fake, CreateSiloAddress());

            Assert.Empty(result.Members);
            Assert.Equal(17, result.Version.Version);
            Assert.Equal("17", result.Version.VersionEtag);
            Assert.Equal(["sync /", "read /", "read /127.0.0.1:11111@12345", "read /"], fake.Calls);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task Read_ConcurrentHeartbeat_PreservesCanonicalFence(bool pointRead, bool afterHeartbeatRead)
        {
            var (fake, entry) = await CreateNativeTable();
            var originalTime = entry.IAmAliveTime;
            var root = fake.Nodes["/"];
            var row = fake.Nodes[ZooKeeperNativeFake.RowPath(entry.SiloAddress)];
            var interleavingPath = afterHeartbeatRead
                ? ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress)
                : ZooKeeperNativeFake.RowPath(entry.SiloAddress);
            fake.AfterRead = async path =>
            {
                if (path == interleavingPath)
                {
                    fake.AfterRead = null;
                    entry.IAmAliveTime = entry.IAmAliveTime.AddDays(1);
                    await Heartbeat(fake, entry);
                }
            };

            var result = await Read(fake, pointRead ? entry.SiloAddress : null);

            Assert.Equal(afterHeartbeatRead ? originalTime : entry.IAmAliveTime, Assert.Single(result.Members).Item1.IAmAliveTime);
            Assert.Equal(1, result.Version.Version);
            Assert.Equal("1", result.Version.VersionEtag);
            Assert.Equal("0", result.Members[0].Item2);
            Assert.Equal(1, fake.Calls.Count(call => call == "sync /"));
            Assert.Equal(1, fake.Calls.Count(call => call == "read " + ZooKeeperNativeFake.RowPath(entry.SiloAddress)));
            Assert.Equal(1, fake.Calls.Count(call => call == "read " + ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress)));
            Assert.Same(root, fake.Nodes["/"]);
            Assert.Same(row, fake.Nodes[ZooKeeperNativeFake.RowPath(entry.SiloAddress)]);
            Assert.Equal(entry.IAmAliveTime, ZooKeeperBasedMembershipTable.Deserialize<DateTime>(
                fake.Nodes[ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress)].Data));
        }

        [Fact]
        public async Task Read_ParallelMissingRowAndAuthorizationFailure_PropagatesAuthorizationFailure()
        {
            var (fake, entry) = await CreateNativeTable();
            var second = CreateTimedEntry(12346);
            Assert.True(await Insert(fake, second, 1));
            var failure = new KeeperException.NoAuthException();
            fake.BeforeRead = path => path == ZooKeeperNativeFake.RowPath(entry.SiloAddress)
                ? Task.FromException(new KeeperException.NoNodeException(path))
                : path == ZooKeeperNativeFake.RowPath(second.SiloAddress)
                    ? Task.FromException(failure) : Task.CompletedTask;

            var actual = await Record.ExceptionAsync(() => Read(fake));

            Assert.Same(failure, actual);
            Assert.Equal(1, fake.Calls.Count(call => call == "sync /"));
        }

        [Fact]
        public async Task Read_StableMissingHeartbeat_PropagatesFailure()
        {
            var (fake, entry) = await CreateNativeTable();
            var path = ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress);
            fake.Nodes.Remove(path);

            var failure = await Assert.ThrowsAsync<KeeperException.NoNodeException>(() => Read(fake));

            Assert.Equal(path, failure.getPath());
            Assert.Equal(1, fake.Calls.Count(call => call == "sync /"));
        }

        [Fact]
        public async Task Insert_PersistentNodesAndVersion_CommitAtomically()
        {
            var fake = new ZooKeeperNativeFake();
            var entry = CreateTimedEntry();

            Assert.True(await Insert(fake, entry, 0));

            Assert.Collection(Assert.Single(fake.Transactions),
                operation => AssertSet(operation, "/", 0),
                operation => AssertCreate(operation, ZooKeeperNativeFake.RowPath(entry.SiloAddress)),
                operation => AssertCreate(operation, ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress)));
            var result = await Read(fake);
            Assert.Equal(1, result.Version.Version);
            Assert.Equal("0", Assert.Single(result.Members).Item2);
            Assert.Equal(entry.SiloName, result.Members[0].Item1.SiloName);
            Assert.Equal(entry.IAmAliveTime, result.Members[0].Item1.IAmAliveTime);
            Assert.All(fake.Nodes.Values, node => Assert.Equal(0, node.Flags));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Insert_DuplicateOrStaleVersion_RollsBackTransaction(bool duplicate)
        {
            var (fake, entry) = await CreateNativeTable();
            var original = fake.Nodes.ToArray();

            Assert.False(await Insert(fake, duplicate ? entry : CreateTimedEntry(12346), duplicate ? 1 : 0));

            Assert.Equal(original, fake.Nodes.ToArray());
            Assert.Single(fake.Transactions);
        }

        [Theory]
        [InlineData("0", 1)]
        [InlineData("1", 1)]
        [InlineData("0", 0)]
        public async Task UpdateRow_OptimisticPreconditions_ControlAtomicMutation(string etag, int tableVersion)
        {
            var (fake, entry) = await CreateNativeTable();
            var original = fake.Nodes.ToArray();
            entry.Status = SiloStatus.Dead;

            var updated = await ZooKeeperBasedMembershipTable.UpdateRowCoreAsync(
                fake.Operations, entry, etag, new TableVersion(tableVersion + 1, tableVersion.ToString()),
                TestContext.Current.CancellationToken);

            var expected = etag == "0" && tableVersion == 1;
            Assert.Equal(expected, updated);
            Assert.Equal("multi", Assert.Single(fake.Calls));
            Assert.Collection(Assert.Single(fake.Transactions),
                operation => AssertSet(operation, "/", tableVersion),
                operation => AssertSet(operation, ZooKeeperNativeFake.RowPath(entry.SiloAddress), int.Parse(etag)));
            if (!expected)
            {
                Assert.Equal(original, fake.Nodes.ToArray());
            }

            var result = await Read(fake);
            Assert.Equal(expected ? SiloStatus.Dead : SiloStatus.Active, Assert.Single(result.Members).Item1.Status);
            Assert.Equal(expected ? "1" : "0", result.Members[0].Item2);
            Assert.Equal(expected ? 2 : 1, result.Version.Version);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task UpdateRow_HeartbeatPreservesOriginalTokens_ForStatusAndVotes(bool voteUpdate, bool concurrentHeartbeat)
        {
            var (fake, entry) = await CreateNativeTable();
            var snapshot = await Read(fake);
            var (update, etag) = Assert.Single(snapshot.Members);
            var next = snapshot.Version.Next();
            var heartbeat = CreateTimedEntry();
            heartbeat.IAmAliveTime = entry.IAmAliveTime.AddDays(1);
            if (concurrentHeartbeat)
            {
                fake.BeforeMulti = async _ =>
                {
                    fake.BeforeMulti = null;
                    await Heartbeat(fake, heartbeat);
                };
            }
            else
            {
                await Heartbeat(fake, heartbeat);
            }

            if (voteUpdate)
            {
                update.SuspectTimes = [Tuple.Create(CreateTimedEntry(12346).SiloAddress, heartbeat.IAmAliveTime)];
            }
            else
            {
                update.Status = SiloStatus.Dead;
            }

            fake.Calls.Clear();

            Assert.True(await ZooKeeperBasedMembershipTable.UpdateRowCoreAsync(
                fake.Operations, update, etag, next, TestContext.Current.CancellationToken));

            Assert.Equal(concurrentHeartbeat ? ["multi", "write " + ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress)] : ["multi"], fake.Calls);
            Assert.Collection(Assert.Single(fake.Transactions),
                operation => AssertSet(operation, "/", snapshot.Version.Version),
                operation => AssertSet(operation, ZooKeeperNativeFake.RowPath(entry.SiloAddress), int.Parse(etag)));
            Assert.Equal(1, fake.Nodes[ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress)].Version);

            var result = await Read(fake);
            var row = Assert.Single(result.Members);
            Assert.Equal(update.Status, row.Item1.Status);
            Assert.Equal(update.SuspectTimes, row.Item1.SuspectTimes);
            Assert.Equal(heartbeat.IAmAliveTime, row.Item1.IAmAliveTime);
            Assert.Equal("1", row.Item2);
            Assert.Equal(2, result.Version.Version);
        }

        [Fact]
        public async Task UpdateRow_PreservesSeparateHeartbeatNode()
        {
            var (fake, entry) = await CreateNativeTable();
            var heartbeat = fake.Nodes[ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress)];
            entry.IAmAliveTime = entry.IAmAliveTime.AddDays(1);
            entry.Status = SiloStatus.Dead;

            Assert.True(await ZooKeeperBasedMembershipTable.UpdateRowCoreAsync(
                fake.Operations, entry, "0", new TableVersion(2, "1"), TestContext.Current.CancellationToken));

            Assert.Equal("multi", Assert.Single(fake.Calls));
            Assert.Same(heartbeat, fake.Nodes[ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress)]);
            var result = await Read(fake);
            var row = Assert.Single(result.Members);
            Assert.Equal(ZooKeeperBasedMembershipTable.Deserialize<DateTime>(heartbeat.Data), row.Item1.IAmAliveTime);
            Assert.Equal(SiloStatus.Dead, row.Item1.Status);
            Assert.Equal("1", row.Item2);
            Assert.Equal(2, result.Version.Version);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task UpdateRow_ConcurrentMembershipMutation_ReturnsConflictWithoutPartialWrite(bool sameRow)
        {
            var (fake, entry) = await CreateNativeTable();
            fake.BeforeMulti = async _ =>
            {
                fake.BeforeMulti = null;
                if (sameRow)
                {
                    var winner = CreateTimedEntry();
                    winner.Status = SiloStatus.ShuttingDown;
                    Assert.True(await ZooKeeperBasedMembershipTable.UpdateRowCoreAsync(
                        fake.Operations, winner, "0", new TableVersion(2, "1"), TestContext.Current.CancellationToken));
                }
                else
                {
                    Assert.True(await Insert(fake, CreateTimedEntry(12346), 1));
                }
            };
            entry.Status = SiloStatus.Dead;
            entry.IAmAliveTime = entry.IAmAliveTime.AddDays(3);

            Assert.False(await ZooKeeperBasedMembershipTable.UpdateRowCoreAsync(
                fake.Operations, entry, "0", new TableVersion(2, "1"), TestContext.Current.CancellationToken));

            var result = await Read(fake, entry.SiloAddress);
            Assert.Equal(sameRow ? SiloStatus.ShuttingDown : SiloStatus.Active, Assert.Single(result.Members).Item1.Status);
            Assert.Equal(DateTime.UnixEpoch.AddDays(1), result.Members[0].Item1.IAmAliveTime);
            Assert.Equal(2, result.Version.Version);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public async Task UpdateIAmAlive_OwnerTimestamp_PerformsOneBlindColumnWrite(int seconds)
        {
            var (fake, entry) = await CreateNativeTable();
            var rowPath = ZooKeeperNativeFake.RowPath(entry.SiloAddress);
            var heartbeatPath = ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress);
            var row = fake.Nodes[rowPath];
            var table = fake.Nodes["/"];
            var heartbeat = fake.Nodes[heartbeatPath];
            entry.IAmAliveTime = entry.IAmAliveTime.AddSeconds(seconds);
            entry.Status = SiloStatus.Dead;
            entry.HostName = "changed-host";
            entry.SiloName = "changed-silo";
            entry.ProxyPort = 123;
            entry.StartTime = entry.StartTime.AddDays(1);
            entry.SuspectTimes = [Tuple.Create(CreateTimedEntry(12346).SiloAddress, entry.IAmAliveTime)];

            await Heartbeat(fake, entry);

            AssertSingleHeartbeatWrite(fake, entry);
            Assert.Equal(3, fake.Nodes.Count);
            Assert.Same(table, fake.Nodes["/"]);
            Assert.Same(row, fake.Nodes[rowPath]);
            Assert.Equal(entry.IAmAliveTime, ZooKeeperBasedMembershipTable.Deserialize<DateTime>(fake.Nodes[heartbeatPath].Data));
            Assert.Equal(heartbeat.Version + 1, fake.Nodes[heartbeatPath].Version);
            Assert.Equal(heartbeat.ChildrenVersion, fake.Nodes[heartbeatPath].ChildrenVersion);
            Assert.Equal(heartbeat.Flags, fake.Nodes[heartbeatPath].Flags);
        }

        [Theory]
        [InlineData("auth")]
        [InlineData("connection")]
        [InlineData("session")]
        [InlineData("version")]
        public async Task UpdateIAmAlive_NativeFailure_PropagatesAfterOneBlindWrite(string kind)
        {
            var (fake, entry) = await CreateNativeTable();
            var original = fake.Nodes.ToArray();
            Exception failure = kind switch
            {
                "auth" => new KeeperException.NoAuthException(),
                "connection" => new KeeperException.ConnectionLossException(),
                "session" => new KeeperException.SessionExpiredException(),
                "version" => new KeeperException.BadVersionException(ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress)),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            fake.BeforeWrite = _ => Task.FromException(failure);

            var actual = await Record.ExceptionAsync(() => Heartbeat(fake, entry));

            Assert.Same(failure, actual);
            AssertSingleHeartbeatWrite(fake, entry);
            Assert.Equal(original, fake.Nodes.ToArray());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task UpdateIAmAlive_MissingNode_PropagatesNativeFailure(bool missingCluster)
        {
            var (fake, entry) = await CreateNativeTable(SiloStatus.Dead);
            if (missingCluster)
            {
                fake.Nodes.Clear();
            }
            else
            {
                await ZooKeeperBasedMembershipTable.CleanupCoreAsync(
                    fake.Operations, entry.IAmAliveTime.AddDays(1), TestContext.Current.CancellationToken);
            }

            fake.Calls.Clear();
            fake.Transactions.Clear();
            var original = fake.Nodes.ToArray();

            var failure = await Assert.ThrowsAsync<KeeperException.NoNodeException>(() => Heartbeat(fake, entry));

            Assert.Equal(ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress), failure.getPath());
            AssertSingleHeartbeatWrite(fake, entry);
            Assert.Equal(original, fake.Nodes.ToArray());
        }

        [Fact]
        public async Task UpdateIAmAlive_CanceledCaller_PreservesPendingNativeWrite()
        {
            var (fake, entry) = await CreateNativeTable();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completeWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.BeforeWrite = _ =>
            {
                writing.SetResult();
                return completeWrite.Task;
            };
            var heartbeatPath = ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress);
            var originalHeartbeat = fake.Nodes[heartbeatPath];
            entry.IAmAliveTime = entry.IAmAliveTime.AddSeconds(1);
            var operation = ZooKeeperBasedMembershipTable.UpdateIAmAliveCoreAsync(entry, fake.SetData, cancellation.Token);
            var caller = ZooKeeperBasedMembershipTable.AwaitOperationAsync(operation, cancellation.Token);
            try
            {
                await writing.Task.WaitAsync(TestContext.Current.CancellationToken);
                cancellation.Cancel();

                var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller);

                Assert.Equal(cancellation.Token, failure.CancellationToken);
                AssertSingleHeartbeatWrite(fake, entry);
                Assert.False(operation.IsCompleted);
                Assert.Same(originalHeartbeat, fake.Nodes[heartbeatPath]);
            }
            finally
            {
                completeWrite.SetResult();
                await operation;
            }

            AssertSingleHeartbeatWrite(fake, entry);
            Assert.Equal(entry.IAmAliveTime, ZooKeeperBasedMembershipTable.Deserialize<DateTime>(fake.Nodes[heartbeatPath].Data));
            Assert.Equal(originalHeartbeat.Version + 1, fake.Nodes[heartbeatPath].Version);
        }

        [Theory]
        [InlineData(SiloStatus.None)]
        [InlineData(SiloStatus.Created)]
        [InlineData(SiloStatus.Joining)]
        [InlineData(SiloStatus.Active)]
        [InlineData(SiloStatus.ShuttingDown)]
        [InlineData(SiloStatus.Stopping)]
        [InlineData(SiloStatus.Dead)]
        public async Task Cleanup_OnlyEligibleDeadRows_DeletesWithNativeVersionChecks(SiloStatus status)
        {
            var (fake, entry) = await CreateNativeTable(status);
            var root = fake.Nodes["/"];

            await ZooKeeperBasedMembershipTable.CleanupCoreAsync(
                fake.Operations, DateTime.UnixEpoch.AddDays(2), TestContext.Current.CancellationToken);

            Assert.Equal(root.Version, fake.Nodes["/"].Version);
            if (status == SiloStatus.Dead)
            {
                Assert.Equal("/", Assert.Single(fake.Nodes).Key);
                Assert.Collection(Assert.Single(fake.Transactions),
                    operation => AssertDelete(operation, ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress), 0),
                    operation => AssertDelete(operation, ZooKeeperNativeFake.RowPath(entry.SiloAddress), 0));
            }
            else
            {
                Assert.Equal(3, fake.Nodes.Count);
                Assert.Empty(fake.Transactions);
                Assert.Equal(status, Assert.Single((await Read(fake)).Members).Item1.Status);
            }
        }

        [Theory]
        [InlineData("start", -1)]
        [InlineData("start", 0)]
        [InlineData("start", 1)]
        [InlineData("heartbeat", -1)]
        [InlineData("heartbeat", 0)]
        [InlineData("heartbeat", 1)]
        [InlineData("vote", -1)]
        [InlineData("vote", 0)]
        [InlineData("vote", 1)]
        public async Task Cleanup_UsesLatestEvidenceAndExactUtcCutoff(string evidence, int ticks)
        {
            var fake = new ZooKeeperNativeFake();
            var entry = CreateTimedEntry();
            entry.Status = SiloStatus.Dead;
            var cutoff = new DateTimeOffset(1970, 1, 3, 7, 0, 0, TimeSpan.FromHours(7));
            var time = cutoff.UtcDateTime.AddTicks(ticks);
            if (evidence == "start")
            {
                entry.StartTime = time;
            }
            else if (evidence == "heartbeat")
            {
                entry.IAmAliveTime = time;
            }
            else
            {
                entry.SuspectTimes =
                [
                    Tuple.Create(CreateSiloAddress(), time),
                    Tuple.Create(CreateTimedEntry(12346).SiloAddress, DateTime.UnixEpoch)
                ];
            }

            Assert.True(await Insert(fake, entry, 0));
            fake.Transactions.Clear();

            await ZooKeeperBasedMembershipTable.CleanupCoreAsync(fake.Operations, cutoff, TestContext.Current.CancellationToken);

            Assert.Equal(ticks >= 0, fake.Nodes.ContainsKey(ZooKeeperNativeFake.RowPath(entry.SiloAddress)));
            Assert.Equal(ticks >= 0, fake.Nodes.ContainsKey(ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress)));
            Assert.Equal(1, fake.Nodes["/"].Version);
            Assert.Equal(ticks < 0 ? 1 : 0, fake.Transactions.Count);
        }

        [Theory]
        [InlineData("heartbeat")]
        [InlineData("start")]
        [InlineData("vote")]
        [InlineData("status")]
        public async Task Cleanup_ConcurrentEvidenceUpdate_PreservesBothNodes(string evidence)
        {
            var (fake, entry) = await CreateNativeTable(SiloStatus.Dead);
            var cutoff = DateTime.UnixEpoch.AddDays(2);
            fake.BeforeMulti = async _ =>
            {
                fake.BeforeMulti = null;
                if (evidence == "heartbeat")
                {
                    entry.IAmAliveTime = cutoff;
                    await Heartbeat(fake, entry);
                }
                else
                {
                    if (evidence == "start")
                    {
                        entry.StartTime = cutoff;
                    }
                    else if (evidence == "vote")
                    {
                        entry.SuspectTimes = [Tuple.Create(CreateSiloAddress(), cutoff)];
                    }
                    else
                    {
                        entry.Status = SiloStatus.Active;
                    }

                    Assert.True(await ZooKeeperBasedMembershipTable.UpdateRowCoreAsync(
                        fake.Operations, entry, "0", new TableVersion(2, "1"), TestContext.Current.CancellationToken));
                }
            };

            await ZooKeeperBasedMembershipTable.CleanupCoreAsync(fake.Operations, cutoff, TestContext.Current.CancellationToken);

            Assert.Equal(3, fake.Nodes.Count);
            Assert.Equal(evidence == "heartbeat" ? 1 : 2, fake.Nodes["/"].Version);
            var row = Assert.Single((await Read(fake)).Members).Item1;
            Assert.Equal(entry.Status, row.Status);
            Assert.Equal(entry.StartTime, row.StartTime);
            Assert.Equal(entry.IAmAliveTime, row.IAmAliveTime);
            Assert.Equal(entry.SuspectTimes, row.SuspectTimes);
            Assert.Collection(fake.Transactions[0],
                operation => AssertDelete(operation, ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress), 0),
                operation => AssertDelete(operation, ZooKeeperNativeFake.RowPath(entry.SiloAddress), 0));
        }

        [Fact]
        public async Task Cleanup_ConcurrentCleaners_CompleteWithUnchangedTableVersion()
        {
            var (fake, _) = await CreateNativeTable(SiloStatus.Dead);
            var cutoff = DateTime.UnixEpoch.AddDays(2);
            fake.BeforeMulti = async _ =>
            {
                fake.BeforeMulti = null;
                await ZooKeeperBasedMembershipTable.CleanupCoreAsync(fake.Operations, cutoff, TestContext.Current.CancellationToken);
            };

            await ZooKeeperBasedMembershipTable.CleanupCoreAsync(fake.Operations, cutoff, TestContext.Current.CancellationToken);

            Assert.Single(fake.Nodes);
            Assert.Equal(1, fake.Nodes["/"].Version);
            Assert.Equal(2, fake.Transactions.Count);
        }

        [Theory]
        [InlineData("read")]
        [InlineData("point")]
        [InlineData("insert")]
        [InlineData("update")]
        [InlineData("cleanup")]
        public async Task MembershipOperations_MissingClusterHistory_PropagatesNoNode(string operation)
        {
            var fake = new ZooKeeperNativeFake();
            fake.Nodes.Clear();

            var failure = await Assert.ThrowsAsync<KeeperException.NoNodeException>(
                () => InvokeNative(operation, fake, CreateTimedEntry(), TestContext.Current.CancellationToken));

            Assert.Equal("/", failure.getPath());
            Assert.Empty(fake.Nodes);
        }

        [Theory]
        [InlineData("read", "auth", false)]
        [InlineData("point", "connection", false)]
        [InlineData("insert", "session", true)]
        [InlineData("update", "auth", true)]
        [InlineData("update", "connection", true)]
        [InlineData("update", "session", true)]
        [InlineData("cleanup", "session", false)]
        [InlineData("cleanup", "auth", true)]
        [InlineData("cleanup", "connection", true)]
        [InlineData("cleanup", "session", true)]
        public async Task MembershipOperations_InfrastructureFailure_PropagatesSameException(string operation, string kind, bool duringWrite)
        {
            var (fake, entry) = await CreateNativeTable(SiloStatus.Dead);
            Exception failure = kind switch
            {
                "auth" => new KeeperException.NoAuthException(),
                "session" => new KeeperException.SessionExpiredException(),
                _ => new KeeperException.ConnectionLossException()
            };
            if (!duringWrite)
            {
                fake.BeforeRead = _ => Task.FromException(failure);
            }

            fake.BeforeMulti = _ => Task.FromException(failure);
            fake.BeforeWrite = _ => Task.FromException(failure);
            entry.IAmAliveTime = entry.IAmAliveTime.AddHours(1);

            var actual = await Record.ExceptionAsync(() => InvokeNative(operation, fake, entry, TestContext.Current.CancellationToken));

            Assert.Same(failure, actual);
            Assert.Equal(3, fake.Nodes.Count);
            Assert.Equal(1, fake.Nodes["/"].Version);
        }

        [Theory]
        [InlineData("update")]
        [InlineData("cleanup")]
        public async Task MembershipOperations_ClusterRemovedDuringWrite_PropagatesNoNode(string operation)
        {
            var (fake, entry) = await CreateNativeTable(SiloStatus.Dead);
            Task RemoveCluster()
            {
                fake.Nodes.Clear();
                return Task.CompletedTask;
            }

            fake.BeforeMulti = _ => RemoveCluster();
            fake.BeforeWrite = _ => RemoveCluster();
            entry.IAmAliveTime = entry.IAmAliveTime.AddHours(1);

            var failure = await Assert.ThrowsAsync<KeeperException.NoNodeException>(
                () => InvokeNative(operation, fake, entry, TestContext.Current.CancellationToken));

            Assert.Equal("/", failure.getPath());
            Assert.Empty(fake.Nodes);
        }

        [Theory]
        [InlineData("cleanup")]
        public async Task NativeOperations_CanceledConflict_StopsRetrying(string operation)
        {
            var (fake, entry) = await CreateNativeTable(SiloStatus.Dead);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var requestCount = 0;
            Task Conflict()
            {
                requestCount = fake.Calls.Count;
                cancellation.Cancel();
                throw new KeeperException.BadVersionException(ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress));
            }

            fake.BeforeMulti = _ => Conflict();
            fake.BeforeWrite = _ => Conflict();
            entry.IAmAliveTime = entry.IAmAliveTime.AddHours(1);

            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => InvokeNative(operation, fake, entry, cancellation.Token));

            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.Equal(requestCount, fake.Calls.Count);
            Assert.Equal(1, fake.Nodes["/"].Version);
            Assert.Equal(3, fake.Nodes.Count);
        }

        [Theory]
        [InlineData("read")]
        [InlineData("point")]
        [InlineData("insert")]
        [InlineData("update")]
        [InlineData("heartbeat")]
        [InlineData("cleanup")]
        public async Task NativeOperations_PreCanceledToken_IssuesNoRequests(string operation)
        {
            var fake = new ZooKeeperNativeFake();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.Cancel();

            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => InvokeNative(operation, fake, CreateTimedEntry(), cancellation.Token));

            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.Empty(fake.Calls);
        }

        [Theory]
        [InlineData("read")]
        [InlineData("point")]
        [InlineData("cleanup")]
        public async Task NativeOperations_CanceledAfterRead_StartsNoFurtherRequests(string operation)
        {
            var (fake, entry) = await CreateNativeTable(SiloStatus.Dead);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            fake.AfterRead = _ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            };
            entry.IAmAliveTime = entry.IAmAliveTime.AddDays(1);

            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => InvokeNative(operation, fake, entry, cancellation.Token));

            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.Equal(1, fake.Calls.Count(call => call.StartsWith("read ", StringComparison.Ordinal)));
            Assert.Empty(fake.Transactions);
            Assert.DoesNotContain(fake.Calls, call => call.StartsWith("write ", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AwaitOperation_CanceledCaller_PreservesRequestAndDisposalOwnership(bool fail)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = new KeeperException.ConnectionLossException();
            async Task<int> NativeOperation()
            {
                try
                {
                    await request.Task;
                    if (fail)
                    {
                        throw failure;
                    }

                    return 42;
                }
                finally
                {
                    disposing.SetResult();
                    await disposal.Task;
                }
            }

            var operation = NativeOperation();
            var caller = ZooKeeperBasedMembershipTable.AwaitOperationAsync(operation, cancellation.Token);
            cancellation.Cancel();
            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller);
            Assert.Equal(cancellation.Token, canceled.CancellationToken);
            Assert.False(operation.IsCompleted);
            Assert.False(disposing.Task.IsCompleted);

            request.SetResult();
            await disposing.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(operation.IsCompleted);
            disposal.SetResult();
            if (fail)
            {
                Assert.Same(failure, await Record.ExceptionAsync(() => operation));
            }
            else
            {
                Assert.Equal(42, await operation);
            }
        }

        [Theory]
        [InlineData("MissingMethod", "expected a parameterless instance method")]
        [InlineData("ToString", "expected System.Int32, received System.String")]
        public void NativeFake_IncompatibleSdkMember_ReportsActionableDiagnostic(string methodName, string detail)
        {
            var failure = Assert.Throws<InvalidOperationException>(() =>
                ZooKeeperNativeFake.GetValue<int>(new object(), methodName));

            Assert.Contains($"System.Object.{methodName}", failure.Message, StringComparison.Ordinal);
            Assert.Contains(detail, failure.Message, StringComparison.Ordinal);
            Assert.Contains("Update the fake for the installed ZooKeeperNetEx API", failure.Message, StringComparison.Ordinal);
            Assert.Contains(typeof(Op).Assembly.FullName!, failure.Message, StringComparison.Ordinal);
        }

        private static Task InvokeNative(string operation, ZooKeeperNativeFake fake, MembershipEntry entry, CancellationToken cancellationToken) =>
            operation switch
            {
                "read" => ZooKeeperBasedMembershipTable.ReadCoreAsync(fake.Operations, null, cancellationToken),
                "point" => ZooKeeperBasedMembershipTable.ReadCoreAsync(fake.Operations, entry.SiloAddress, cancellationToken),
                "insert" => ZooKeeperBasedMembershipTable.InsertRowCoreAsync(fake.Operations, entry, new TableVersion(2, "1"), cancellationToken),
                "update" => ZooKeeperBasedMembershipTable.UpdateRowCoreAsync(fake.Operations, entry, "0", new TableVersion(2, "1"), cancellationToken),
                "heartbeat" => ZooKeeperBasedMembershipTable.UpdateIAmAliveCoreAsync(entry, fake.SetData, cancellationToken),
                "cleanup" => ZooKeeperBasedMembershipTable.CleanupCoreAsync(fake.Operations, DateTime.UnixEpoch.AddDays(3), cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };

        private static async Task<(ZooKeeperNativeFake Fake, MembershipEntry Entry)> CreateNativeTable(SiloStatus status = SiloStatus.Active)
        {
            var fake = new ZooKeeperNativeFake();
            var entry = CreateTimedEntry();
            entry.Status = status;
            Assert.True(await Insert(fake, entry, 0));
            fake.Calls.Clear();
            fake.Transactions.Clear();
            return (fake, entry);
        }

        private static MembershipEntry CreateTimedEntry(int generation = 12345) =>
            new()
            {
                SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), generation),
                HostName = "host-a",
                SiloName = "silo-a",
                Status = SiloStatus.Active,
                StartTime = DateTime.UnixEpoch,
                IAmAliveTime = DateTime.UnixEpoch.AddDays(1)
            };

        private static Task<bool> Insert(ZooKeeperNativeFake fake, MembershipEntry entry, int version) =>
            ZooKeeperBasedMembershipTable.InsertRowCoreAsync(fake.Operations, entry,
                new TableVersion(version + 1, version.ToString()), TestContext.Current.CancellationToken);

        private static Task<MembershipTableData> Read(ZooKeeperNativeFake fake, SiloAddress? address = null) =>
            ZooKeeperBasedMembershipTable.ReadCoreAsync(fake.Operations, address, TestContext.Current.CancellationToken);

        private static Task Heartbeat(ZooKeeperNativeFake fake, MembershipEntry entry) =>
            ZooKeeperBasedMembershipTable.UpdateIAmAliveCoreAsync(entry, fake.SetData, TestContext.Current.CancellationToken);

        private static void AssertSingleHeartbeatWrite(ZooKeeperNativeFake fake, MembershipEntry entry)
        {
            var path = ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress);
            Assert.Equal("write " + path, Assert.Single(fake.Calls));
            var write = Assert.Single(fake.Writes);
            Assert.Equal(path, write.Path);
            Assert.Equal(-1, write.Version);
            Assert.Equal(ZooKeeperBasedMembershipTable.Serialize(entry.IAmAliveTime), write.Data);
            Assert.Empty(fake.Transactions);
        }

        private static void AssertSet(object operation, string path, int version)
        {
            var set = Assert.IsType<ZooKeeperNativeFake.SetDataRequest>(operation);
            Assert.Equal(path, set.Path);
            Assert.Equal(version, set.Version);
        }

        private static void AssertCreate(object operation, string path)
        {
            var create = Assert.IsType<ZooKeeperNativeFake.CreateRequest>(operation);
            Assert.Equal(path, create.Path);
            Assert.Equal(0, create.Flags);
            Assert.Equal(ZooDefs.Ids.OPEN_ACL_UNSAFE, create.Acl);
        }

        private static void AssertDelete(object operation, string path, int version)
        {
            var delete = Assert.IsType<ZooKeeperNativeFake.DeleteRequest>(operation);
            Assert.Equal(path, delete.Path);
            Assert.Equal(version, delete.Version);
        }

        private static ZooKeeperBasedMembershipTable CreateSut(
            string connectionString = "sentinel.invalid:2181",
            string clusterId = "cluster-a") =>
            new(
                NullLogger<ZooKeeperBasedMembershipTable>.Instance,
                CreateMembershipTableOptions(connectionString),
                CreateClusterOptions(clusterId));

        private static IOptions<ZooKeeperClusteringSiloOptions> CreateMembershipTableOptions(
            string connectionString = "sentinel.invalid:2181") =>
            Options.Create(new ZooKeeperClusteringSiloOptions { ConnectionString = connectionString });

        private static IOptions<ClusterOptions> CreateClusterOptions(string clusterId = "cluster-a") =>
            Options.Create(new ClusterOptions { ClusterId = clusterId });

        private static MembershipEntry CreateMembershipEntry() =>
            new()
            {
                SiloAddress = CreateSiloAddress(),
                HostName = "host-a",
                SiloName = "silo-a",
                Status = SiloStatus.Active,
                ProxyPort = 30000
            };

        private static SiloAddress CreateSiloAddress() =>
            SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 12345);

        private static TableVersion CreateTableVersion() => new(18, "17");

        private static T GetPrivateField<T>(object instance, string fieldName)
        {
            var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return Assert.IsType<T>(field.GetValue(instance));
        }

        private static string InvokePrivatePathMethod(string methodName, SiloAddress address)
        {
            var method = typeof(ZooKeeperBasedMembershipTable).GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return Assert.IsType<string>(method.Invoke(null, [address]));
        }
    }
}
