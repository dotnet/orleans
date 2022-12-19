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
    public class RedisGrainDirectoryTests : GrainDirectoryTests<RedisGrainDirectory>
    {
        public RedisGrainDirectoryTests(ITestOutputHelper testOutput) : base(testOutput)
        {
        }

        protected override RedisGrainDirectory GetGrainDirectory()
        {
            TestUtils.CheckForRedis();

            var configuration = TestDefaultConfiguration.RedisConnectionString;
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
}
