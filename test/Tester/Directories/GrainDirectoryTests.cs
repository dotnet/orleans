using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;
using Orleans.Runtime;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Tester.Directories
{
    // Base tests for custom Grain Directory
    public abstract class GrainDirectoryTests<T> where T : IGrainDirectory
    {
        protected T grainDirectory;
        protected readonly ILoggerFactory loggerFactory;

        protected GrainDirectoryTests(ITestOutputHelper testOutput)
        {
            this.loggerFactory = new LoggerFactory();
            this.loggerFactory.AddProvider(new XunitLoggerProvider(testOutput));
            this.grainDirectory = GetGrainDirectory();
        }

        protected abstract T GetGrainDirectory();

        [SkippableFact]
        public async Task RegisterLookupUnregisterLookup()
        {
            var expected = new GrainAddress
            {
                ActivationId = ActivationId.NewId(),
                GrainId = GrainId.Parse("user/someraondomuser_" + Guid.NewGuid().ToString("N")),
                SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678")
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
                ActivationId = ActivationId.NewId(),
                GrainId = GrainId.Parse("user/someraondomuser_" + Guid.NewGuid().ToString("N")),
                SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678")
            };

            var differentActivation = new GrainAddress
            {
                ActivationId = ActivationId.NewId(),
                GrainId = expected.GrainId,
                SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678")
            };

            var differentSilo = new GrainAddress
            {
                ActivationId = expected.ActivationId,
                GrainId = expected.GrainId,
                SiloAddress = SiloAddress.FromParsableString("10.0.23.14:1000@4583")
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
                ActivationId = ActivationId.NewId(),
                GrainId = GrainId.Parse("user/someraondomuser_" + Guid.NewGuid().ToString("N")),
                SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678")
            };

            var otherEntry = new GrainAddress
            {
                ActivationId = ActivationId.NewId(),
                GrainId = expected.GrainId,
                SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678")
            };

            Assert.Equal(expected, await this.grainDirectory.Register(expected));
            await this.grainDirectory.Unregister(otherEntry);
            Assert.Equal(expected, await this.grainDirectory.Lookup(expected.GrainId));
        }

        [SkippableFact]
        public async Task LookupNotFound()
        {
            Assert.Null(await this.grainDirectory.Lookup(GrainId.Parse("user/someraondomuser_" + Guid.NewGuid().ToString("N"))));
        }
    }
}
