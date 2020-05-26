using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.GrainDirectory.Redis;
using Orleans.TestingHost.Utils;
using StackExchange.Redis;
using Tester.Directories;
using TestExtensions;
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
}
