using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.GrainDirectory.AzureStorage;
using Orleans.Runtime;
using Orleans.TestingHost.Utils;
using Tester.Directories;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AzureUtils
{
    [TestCategory("Azure"), TestCategory("Storage")]
    public class AzureTableGrainDirectoryTests : GrainDirectoryTests<AzureTableGrainDirectory>
    {
        public AzureTableGrainDirectoryTests(ITestOutputHelper testOutput) : base(testOutput)
        {
        }

        protected override AzureTableGrainDirectory GetGrainDirectory()
        {
            TestUtils.CheckForAzureStorage();

            var clusterOptions = new ClusterOptions
            {
                ClusterId = Guid.NewGuid().ToString("N"),
                ServiceId = Guid.NewGuid().ToString("N"),
            };

            var directoryOptions = new AzureTableGrainDirectoryOptions();
            directoryOptions.ConfigureTestDefaults();

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
                    ActivationId = ActivationId.NewId(),
                    GrainId = GrainId.Parse("user/someraondomuser_" + Guid.NewGuid().ToString("N")),
                    SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678")
                };
                addresses.Add(addr);
                await this.grainDirectory.Register(addr);
            }

            // Modify the Rth entry locally, to simulate another activation tentative by another silo
            var oldActivation = addresses[R].ActivationId;
            addresses[R].ActivationId = ActivationId.NewId();

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
}
