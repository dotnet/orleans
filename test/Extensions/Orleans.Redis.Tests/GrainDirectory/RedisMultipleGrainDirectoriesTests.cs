using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory.Redis;
using Orleans.TestingHost;
using StackExchange.Redis;
using Tester.Directories;
using TestExtensions;
using UnitTests.Grains.Directories;
using Xunit;

namespace Tester.Redis.GrainDirectory
{
    /// <summary>
    /// Tests for Orleans clusters using multiple grain directories with Redis as the directory storage backend.
    /// </summary>
    [TestCategory("Redis"), TestCategory("Directory"), TestCategory("Functional")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class RedisMultipleGrainDirectoriesTests : MultipleGrainDirectoriesTests
    {
        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder siloBuilder)
            {
                TestUtils.CheckForRedis();

                siloBuilder
                    .AddRedisGrainDirectory(
                        CustomDirectoryGrain.DIRECTORY,
                        options =>
                        {
                            options.ConfigurationOptions = ConfigurationOptions.Parse(TestDefaultConfiguration.RedisConnectionString);
                            options.EntryExpiry = TimeSpan.FromMinutes(5);
                        })
                    .ConfigureLogging(builder => builder.AddFilter(typeof(RedisGrainDirectory).FullName, LogLevel.Debug));
            }
        }

        protected override void CheckPreconditionsOrThrow() => TestUtils.CheckForRedis();

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            base.ConfigureTestCluster(builder);
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }
    }
}
