using System;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
                returnedTask = sut.InsertRow(null!, CreateTableVersion(), TestContext.Current.CancellationToken);
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
                returnedTask = sut.InsertRow(CreateMembershipEntry(), null!, TestContext.Current.CancellationToken);
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
                returnedTask = sut.InsertRow(null!, null!, TestContext.Current.CancellationToken);
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
                returnedTask = sut.UpdateRow(null!, "17", CreateTableVersion(), TestContext.Current.CancellationToken);
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
                returnedTask = sut.UpdateRow(CreateMembershipEntry(), "17", null!, TestContext.Current.CancellationToken);
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
                returnedTask = sut.UpdateRow(CreateMembershipEntry(), null!, CreateTableVersion(), TestContext.Current.CancellationToken);
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
                returnedTask = sut.UpdateRow(null!, "17", null!, TestContext.Current.CancellationToken);
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
                returnedTask = sut.UpdateIAmAlive(null!, TestContext.Current.CancellationToken);
            });

            Assert.Equal("entry", exception.ParamName);
            Assert.Null(returnedTask);
        }

        [Theory]
        [InlineData(nameof(IMembershipTable.InitializeMembershipTable))]
        [InlineData(nameof(IMembershipTable.DeleteMembershipTableEntries))]
        [InlineData(nameof(IMembershipTable.CleanupDefunctSiloEntries))]
        [InlineData(nameof(IMembershipTable.ReadRow))]
        [InlineData(nameof(IMembershipTable.ReadAll))]
        [InlineData(nameof(IMembershipTable.InsertRow))]
        [InlineData(nameof(IMembershipTable.UpdateRow))]
        [InlineData(nameof(IMembershipTable.UpdateIAmAlive))]
        public async Task MembershipOperations_PreCanceledToken_DoesNotCreateClient(string operation)
        {
            var sut = CreateSut("127.0.0.1:invalid-port");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.Cancel();
            var cancellationToken = cancellation.Token;
            Func<Task> invoke = operation switch
            {
                nameof(IMembershipTable.InitializeMembershipTable) => () => sut.InitializeMembershipTable(true, cancellationToken),
                nameof(IMembershipTable.DeleteMembershipTableEntries) => () => sut.DeleteMembershipTableEntries("cluster-a", cancellationToken),
                nameof(IMembershipTable.CleanupDefunctSiloEntries) => () => sut.CleanupDefunctSiloEntries(DateTimeOffset.UnixEpoch, cancellationToken),
                nameof(IMembershipTable.ReadRow) => () => sut.ReadRow(CreateSiloAddress(), cancellationToken),
                nameof(IMembershipTable.ReadAll) => () => sut.ReadAll(cancellationToken),
                nameof(IMembershipTable.InsertRow) => () => sut.InsertRow(CreateMembershipEntry(), CreateTableVersion(), cancellationToken),
                nameof(IMembershipTable.UpdateRow) => () => sut.UpdateRow(CreateMembershipEntry(), "17", CreateTableVersion(), cancellationToken),
                nameof(IMembershipTable.UpdateIAmAlive) => () => sut.UpdateIAmAlive(CreateMembershipEntry(), cancellationToken),
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
