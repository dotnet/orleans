using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Persistence.AzureStorage.Migration.Reminders;
using Orleans.Persistence.Migration;
using Orleans.TestingHost;
using Tester.AzureUtils.Migration.Abstractions;
using Orleans.Persistence.AzureStorage.Migration;
using TesterInternal.AzureInfra;
using Xunit;

namespace Tester.AzureUtils.Migration;

[TestCategory("Functional"), TestCategory("Migration"), TestCategory("Azure"), TestCategory("AzureQueueStorage")]
public class MigrationStreamingAzureQueueSetup : MigrationStreamingAzureQueueTests, IClassFixture<MigrationStreamingAzureQueueSetup.Fixture>
{
    public static Guid Guid = Guid.NewGuid();

    public MigrationStreamingAzureQueueSetup(Fixture fixture) : base(fixture)
    {
        fixture.EnsurePreconditionsMet();
    }

    public static string QueueName => $"migration-queue-{Guid}";

    public class Fixture : BaseAzureTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }
    }

    private class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMigrationTools();

            siloBuilder.AddAzureQueueMigrationStreams("AzureQueueProvider", configurator =>
            {
                configurator.ConfigureAzureQueue(ob => ob.Configure(options =>
                {
                    // TODO replace with configuration connection string
                    options.ConfigureQueueServiceClient("UseDevelopmentStorage=true");
                    options.QueueNames = new List<string> { QueueName };
                }));
            });


        }
    }
}
