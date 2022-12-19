using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory.Redis;
using Orleans.Hosting;
using Orleans.TestingHost;
using StackExchange.Redis;
using Tester.Directories;
using Tester.Redis.Utility;
using TestExtensions;
using UnitTests.Grains.Directories;
using Xunit;
using Xunit.Abstractions;

namespace Tester.Redis.GrainDirectory
{
    [TestCategory("Redis"), TestCategory("Directory"), TestCategory("Functional")]
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
