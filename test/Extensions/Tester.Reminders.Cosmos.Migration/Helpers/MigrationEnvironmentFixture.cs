using Orleans;
using Orleans.ApplicationParts;
using Orleans.Hosting;
using Orleans.Persistence.Migration;
using ProtoBuf.Meta;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;

namespace Tester.Reminders.Cosmos.Migration.Helpers
{
    public class MigrationEnvironmentFixture : SerializationTestEnvironment
    {
        public const string MigrationCollection = "MigrationTestEnvironment";

        public MigrationEnvironmentFixture()
            : base(Setup)
        {
        }

        public static void Setup(IClientBuilder clientBuilder)
        {
            var appPartManager = new ApplicationPartManager()
                .AddApplicationPart(typeof(ExceptionGrain).Assembly)
                .AddApplicationPart(typeof(IExceptionGrain).Assembly)
                .AddApplicationPart(typeof(MigrationEnvironmentFixture).Assembly)
                .WithReferences();

            clientBuilder.ConfigureServices(services =>
            {
                DefaultSiloServices.AddDefaultServices(appPartManager, services);
                services.AddMigrationTools();

                // We don't care about validation really
                var validators = services.Where(x => x.ServiceType.Name.EndsWith("Validator")).ToList();
                foreach (var validator in validators)
                {
                    services.Remove(services.First(x => x == validator));
                }
            });
        }
    }
}
