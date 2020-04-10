using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.GrainDirectory.AzureStorage;
using Orleans.TestingHost.Utils;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils
{
    [TestCategory("Azure"), TestCategory("Storage")]
    public class AzureTableGrainDirectoryTests : GrainDirectoryTests<AzureTableGrainDirectory>
    {
        protected override AzureTableGrainDirectory GetGrainDirectory()
        {
            TestUtils.CheckForAzureStorage();

            var clusterOptions = new ClusterOptions
            {
                ClusterId = Guid.NewGuid().ToString("N"),
                ServiceId = Guid.NewGuid().ToString("N"),
            };

            var directoryOptions = new AzureTableGrainDirectoryOptions
            {
                ConnectionString = TestDefaultConfiguration.DataConnectionString,
            };

            var loggerFactory = TestingUtils.CreateDefaultLoggerFactory("AzureGrainDirectoryTests.log");

            var directory = new AzureTableGrainDirectory(directoryOptions, Options.Create(clusterOptions), loggerFactory);
            directory.InitializeIfNeeded().GetAwaiter().GetResult();

            return directory;
        }

        [SkippableFact]
        public async Task UnregisterMany()
        {
            const int N = 250;
            const int R = 40;

            // Create and insert N entries
            var addresses = new List<GrainAddress>();
            for (var i = 0; i < N; i++)
            {
                var addr = new GrainAddress
                {
                    ActivationId = Guid.NewGuid().ToString("N"),
                    GrainId = "user/someraondomuser_" + Guid.NewGuid().ToString("N"),
                    SiloAddress = "10.0.23.12:1000@5678"
                };
                addresses.Add(addr);
                await this.grainDirectory.Register(addr);
            }

            // Modify the Rth entry locally, to simulate another activation tentative by another silo
            var oldActivation = addresses[R].ActivationId;
            addresses[R].ActivationId = Guid.NewGuid().ToString("N");

            // Batch unregister
            await this.grainDirectory.UnregisterMany(addresses);

            // Now we should only find the old Rth entry
            for (int i = 0; i < N; i++)
            {
                if (i == R)
                {
                    var addr = await this.grainDirectory.Lookup(addresses[i].GrainId);
                    Assert.NotNull(addr);
                    Assert.Equal(oldActivation, addr.ActivationId);
                }
                else
                {
                    Assert.Null(await this.grainDirectory.Lookup(addresses[i].GrainId));
                }
            }
        }
    }

    // TODO Move that into a common project
    public abstract class GrainDirectoryTests<T> where T : IGrainDirectory
    {
        protected T grainDirectory;

        protected GrainDirectoryTests()
        {
            this.grainDirectory = GetGrainDirectory();
        }

        protected abstract T GetGrainDirectory();

        [SkippableFact]
        public async Task RegisterLookupUnregisterLookup()
        {
            var expected = new GrainAddress
            {
                ActivationId = Guid.NewGuid().ToString("N"),
                GrainId = "user/someraondomuser_" + Guid.NewGuid().ToString("N"),
                SiloAddress = "10.0.23.12:1000@5678"
            };

            Assert.Equal(expected, await this.grainDirectory.Register(expected));

            Assert.Equal(expected, await this.grainDirectory.Lookup(expected.GrainId));

            await this.grainDirectory.Unregister(expected);

            Assert.Null(await this.grainDirectory.Lookup(expected.GrainId));
        }

        [SkippableFact]
        public async Task DoNotOverrideEntry()
        {
            var expected = new GrainAddress
            {
                ActivationId = Guid.NewGuid().ToString("N"),
                GrainId = "user/someraondomuser_" + Guid.NewGuid().ToString("N"),
                SiloAddress = "10.0.23.12:1000@5678"
            };

            var differentActivation = new GrainAddress
            {
                ActivationId = Guid.NewGuid().ToString("N"),
                GrainId = expected.GrainId,
                SiloAddress = "10.0.23.12:1000@5678"
            };

            var differentSilo = new GrainAddress
            {
                ActivationId = expected.ActivationId,
                GrainId = expected.GrainId,
                SiloAddress = "10.0.23.14:1000@4583"
            };

            Assert.Equal(expected, await this.grainDirectory.Register(expected));
            Assert.Equal(expected, await this.grainDirectory.Register(differentActivation));
            Assert.Equal(expected, await this.grainDirectory.Register(differentSilo));

            Assert.Equal(expected, await this.grainDirectory.Lookup(expected.GrainId));
        }

        [SkippableFact]
        public async Task DoNotDeleteDifferentActivationIdEntry()
        {
            var expected = new GrainAddress
            {
                ActivationId = Guid.NewGuid().ToString("N"),
                GrainId = "user/someraondomuser_" + Guid.NewGuid().ToString("N"),
                SiloAddress = "10.0.23.12:1000@5678"
            };

            var otherEntry = new GrainAddress
            {
                ActivationId = Guid.NewGuid().ToString("N"),
                GrainId = expected.GrainId,
                SiloAddress = "10.0.23.12:1000@5678"
            };

            Assert.Equal(expected, await this.grainDirectory.Register(expected));
            await this.grainDirectory.Unregister(otherEntry);
            Assert.Equal(expected, await this.grainDirectory.Lookup(expected.GrainId));
        }

        [SkippableFact]
        public async Task LookupNotFound()
        {
            Assert.Null(await this.grainDirectory.Lookup("user/someraondomuser_" + Guid.NewGuid().ToString("N")));
        }
    }
}
