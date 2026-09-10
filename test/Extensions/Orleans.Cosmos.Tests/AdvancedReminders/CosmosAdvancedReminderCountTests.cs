#nullable enable
using System.Net;
using System.Reflection;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.AdvancedReminders.Cosmos;
using Orleans.Configuration;
using Xunit;

namespace UnitTests.AdvancedRemindersTest;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
public class CosmosAdvancedReminderCountTests
{
    [Fact]
    public async Task Count_UsesAParameterizedAggregateForOneService()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var container = Substitute.For<Container>();
        QueryDefinition? query = null;
        using var iterator = new CountIterator(TestContext.Current.CancellationToken);
        container.GetItemQueryIterator<long>(Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(call =>
            {
                query = call.Arg<QueryDefinition>();
                return iterator;
            });
        var table = new CosmosReminderTable(NullLoggerFactory.Instance, services,
            Options.Create(new CosmosReminderTableOptions()), Options.Create(new ClusterOptions { ServiceId = "service" }));
        typeof(CosmosReminderTable).GetField("_container", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(table, container);

        Assert.Equal(34_900, await table.GetCountAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(query);
        Assert.Equal("SELECT VALUE COUNT(1) FROM r WHERE r.ServiceId = @serviceId", query.QueryText);
        var parameter = Assert.Single(query.GetQueryParameters());
        Assert.Equal("@serviceId", parameter.Name);
        Assert.Equal("service", parameter.Value);
        Assert.Equal(2, iterator.ReadCount);
        Assert.Single(container.ReceivedCalls());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => table.GetCountAsync(new CancellationToken(canceled: true)));
        Assert.Single(container.ReceivedCalls());
    }

    private sealed class CountIterator(CancellationToken expectedToken) : FeedIterator<long>
    {
        public int ReadCount { get; private set; }
        public override bool HasMoreResults => ReadCount < 2;
        public override Task<FeedResponse<long>> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            Assert.Equal(expectedToken, cancellationToken);
            ReadCount++;
            return Task.FromResult<FeedResponse<long>>(new CountResponse(ReadCount == 1 ? [] : [34_900]));
        }
    }

    private sealed class CountResponse(IReadOnlyList<long> counts) : FeedResponse<long>
    {
        public override int Count => counts.Count;
        public override string ContinuationToken => null!;
        public override string IndexMetrics => null!;
        public override Headers Headers => null!;
        public override IEnumerable<long> Resource => counts;
        public override HttpStatusCode StatusCode => HttpStatusCode.OK;
        public override double RequestCharge => 0;
        public override string ActivityId => string.Empty;
        public override string ETag => null!;
        public override CosmosDiagnostics Diagnostics => null!;
        public override IEnumerator<long> GetEnumerator() => counts.GetEnumerator();
    }
}
