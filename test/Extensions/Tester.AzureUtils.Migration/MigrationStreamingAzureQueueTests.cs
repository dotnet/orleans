using Orleans.Hosting;
using Orleans.Persistence.Migration;
using Orleans.TestingHost;
using Tester.AzureUtils.Migration.Abstractions;
using Xunit;
using Orleans.Streaming.Migration.Configuration;

namespace Tester.AzureUtils.Migration;

[TestCategory("Functional"), TestCategory("Migration"), TestCategory("Azure"), TestCategory("AzureQueueStorage")]
public class MigrationStreamingAzureQueueSetup : MigrationStreamingAzureQueueTests, IClassFixture<MigrationStreamingAzureQueueSetup.Fixture>
{
    public static Guid Guid = Guid.NewGuid();

    public MigrationStreamingAzureQueueSetup(Fixture fixture) : base(fixture)
    {
        fixture.EnsurePreconditionsMet();
    }

    public static string StreamProviderName = "AzureQueueProvider";
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

            // required for Orleans streaming
            siloBuilder.AddMemoryGrainStorage("PubSubStore");

            // Migration registration
            siloBuilder.AddAzureQueueMigrationStreams(StreamProviderName, configurator =>
            {
                configurator.ConfigureAzureQueue(ob => ob.Configure(options =>
                {
                    // TODO replace with configuration connection string
                    options.ConfigureQueueServiceClient("UseDevelopmentStorage=true");
                    options.QueueNames = new List<string> { QueueName };

                    options.SerializationMode = SerializationMode.PrioritizeJson;
                }));
            });

            // NON MIGRATION
            //siloBuilder.AddAzureQueueStreams(StreamProviderName, configurator =>
            //{
            //    configurator.ConfigureAzureQueue(ob => ob.Configure(options =>
            //    {
            //        // TODO replace with configuration connection string
            //        options.ConfigureQueueServiceClient("UseDevelopmentStorage=true");
            //        options.QueueNames = new List<string> { QueueName };
            //    }));
            //});


        }
    }
}
