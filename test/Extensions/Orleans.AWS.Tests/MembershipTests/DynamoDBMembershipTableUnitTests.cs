using System.Globalization;
using System.Net;
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
