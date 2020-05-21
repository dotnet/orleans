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

namespace Tester.Redis
{
    [TestCategory("Redis"), TestCategory("Directory")]
    public class RedisGrainDirectoryTests : GrainDirectoryTests<RedisGrainDirectory>
    {
        protected override RedisGrainDirectory GetGrainDirectory()
        {
            var configuration = TestDefaultConfiguration.RedisConnectionString;

            if (string.IsNullOrWhiteSpace(configuration))
            {
                throw new SkipException("No connection string found. Skipping");
            }

            var options = new RedisGrainDirectoryOptions
            {
                ConfigurationOptions = ConfigurationOptions.Parse(configuration),
            };

            var directory = new RedisGrainDirectory(options);
            directory.Initialize().GetAwaiter().GetResult();
            return directory;
        }
    }
}
