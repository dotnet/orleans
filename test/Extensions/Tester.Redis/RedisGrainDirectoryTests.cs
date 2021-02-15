using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory.Redis;
using Orleans.Hosting;
using Orleans.TestingHost;
using StackExchange.Redis;
using Tester.Directories;
using TestExtensions;
using UnitTests.Grains.Directories;
using Xunit;
using Xunit.Abstractions;

namespace Tester.Redis
{
    [TestCategory("Redis"), TestCategory("Directory")]
    public class RedisGrainDirectoryTests : GrainDirectoryTests<RedisGrainDirectory>
    {
        public RedisGrainDirectoryTests(ITestOutputHelper testOutput)
            : base(testOutput)
        {
        }

        protected override RedisGrainDirectory GetGrainDirectory()
        {
            var configuration = TestDefaultConfiguration.RedisConnectionString;

            if (string.IsNullOrWhiteSpace(configuration))
            {
                throw new SkipException("No connection string found. Skipping");
            }

            var directoryOptions = new RedisGrainDirectoryOptions
            {
                ConfigurationOptions = ConfigurationOptions.Parse(configuration),
                EntryExpiry = TimeSpan.FromMinutes(1),
            };

            var clusterOptions = Options.Create(new ClusterOptions { ServiceId = "SomeServiceId", ClusterId = Guid.NewGuid().ToString("N") });
            var directory = new RedisGrainDirectory(
                directoryOptions,
                clusterOptions,
                this.loggerFactory.CreateLogger<RedisGrainDirectory>());
            directory.Initialize().GetAwaiter().GetResult();
            return directory;
        }
    }

    [TestCategory("Redis")]
    public class RedisMultipleGrainDirectoriesTests : MultipleGrainDirectoriesTests
    {
        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder siloBuilder)
            {
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

        protected override void CheckPreconditionsOrThrow()
        {
            if (string.IsNullOrWhiteSpace(TestDefaultConfiguration.RedisConnectionString))
            {
                throw new SkipException("TestDefaultConfiguration.RedisConnectionString is empty");
            }
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            base.ConfigureTestCluster(builder);
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }
    }
}
