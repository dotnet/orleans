using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.TestingHost;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskJournalFormatTests
{
    [Fact]
    public async Task SiloStartupFailsBeforeActivationWhenJournalFormatCannotStoreTaskState()
    {
        var builder = new InProcessTestClusterBuilder();
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.AddJournaling();
            siloBuilder.AddVolatileJournalStorage();
            siloBuilder.UseInMemoryDurableJobs();
            siloBuilder.AddDurableTasks();
            siloBuilder.Services.AddSingleton<DurableTaskRecoveryProbe>();
            siloBuilder.Services.AddSingleton<DurableTaskMessagingProbe>();
        });
        await using var cluster = builder.Build();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => cluster.DeployAsync(TestContext.Current.CancellationToken));

        Assert.Contains(
            "Durable Tasks stores task state with Orleans serialization and requires the Orleans binary journal format.",
            exception.ToString(),
            StringComparison.Ordinal);
    }
}
