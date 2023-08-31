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
                GrainId = GrainId.Parse("user/somerandomuser_" + Guid.NewGuid().ToString("N")),
                SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
                MembershipVersion = new MembershipVersion(51)
            };

            Assert.Equal(expected, await this.grainDirectory.Register(expected, null, CancellationToken.None));

            Assert.Equal(expected, await this.grainDirectory.Lookup(expected.GrainId, CancellationToken.None));

            await this.grainDirectory.Unregister(expected, CancellationToken.None);

            Assert.Null(await this.grainDirectory.Lookup(expected.GrainId, CancellationToken.None));
        }

        [SkippableFact]
        public async Task DoNotOverwriteEntry()
        {
            var expected = new GrainAddress
            {
                ActivationId = ActivationId.NewId(),
                GrainId = GrainId.Parse("user/somerandomuser_" + Guid.NewGuid().ToString("N")),
                SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
                MembershipVersion = new MembershipVersion(51)
            };

            var differentActivation = new GrainAddress
            {
                ActivationId = ActivationId.NewId(),
                GrainId = expected.GrainId,
                SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
                MembershipVersion = new MembershipVersion(51)
            };

            var differentSilo = new GrainAddress
            {
                ActivationId = expected.ActivationId,
                GrainId = expected.GrainId,
                SiloAddress = SiloAddress.FromParsableString("10.0.23.14:1000@4583"),
                MembershipVersion = new MembershipVersion(51)
            };

            Assert.Equal(expected, await this.grainDirectory.Register(expected, null, CancellationToken.None));
            Assert.Equal(expected, await this.grainDirectory.Register(differentActivation, null, CancellationToken.None));
            Assert.Equal(expected, await this.grainDirectory.Register(differentSilo, null, CancellationToken.None));

            Assert.Equal(expected, await this.grainDirectory.Lookup(expected.GrainId, CancellationToken.None));
        }

        /// <summary>
        /// Overwrite an existing entry if the register call includes a matching "previousAddress" parameter.
        /// </summary>
        [SkippableFact]
        public async Task OverwriteEntryIfMatch()
        {
            var initial = new GrainAddress
            {
                ActivationId = ActivationId.NewId(),
                GrainId = GrainId.Parse("user/somerandomuser_" + Guid.NewGuid().ToString("N")),
                SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
                MembershipVersion = new MembershipVersion(51)
            };

            var differentActivation = new GrainAddress
            {
                ActivationId = ActivationId.NewId(),
                GrainId = initial.GrainId,
                SiloAddress = initial.SiloAddress,
                MembershipVersion = initial.MembershipVersion
            };

            var differentSilo = new GrainAddress
            {
                ActivationId = initial.ActivationId,
                GrainId = initial.GrainId,
                SiloAddress = SiloAddress.FromParsableString("10.0.23.14:1000@4583"),
                MembershipVersion = initial.MembershipVersion
            };

            // Success, no registration exists, so the previous address is ignored.
            Assert.Equal(initial, await this.grainDirectory.Register(initial, differentSilo, CancellationToken.None));

            // Success, the previous address matches the existing registration.
            Assert.Equal(differentActivation, await this.grainDirectory.Register(differentActivation, initial, CancellationToken.None));

            // Failure, the previous address does not match the existing registration.
            Assert.Equal(differentActivation, await this.grainDirectory.Register(differentSilo, initial, CancellationToken.None));

            Assert.Equal(differentActivation, await this.grainDirectory.Lookup(initial.GrainId, CancellationToken.None));
        }

        [SkippableFact]
        public async Task DoNotDeleteDifferentActivationIdEntry()
        {
            var expected = new GrainAddress
            {
                ActivationId = ActivationId.NewId(),
                GrainId = GrainId.Parse("user/somerandomuser_" + Guid.NewGuid().ToString("N")),
                SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
                MembershipVersion = new MembershipVersion(51)
            };

            var otherEntry = new GrainAddress
            {
                ActivationId = ActivationId.NewId(),
                GrainId = expected.GrainId,
                SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
                MembershipVersion = new MembershipVersion(51)
            };

            Assert.Equal(expected, await this.grainDirectory.Register(expected, null, CancellationToken.None));
            await this.grainDirectory.Unregister(otherEntry, CancellationToken.None);
            Assert.Equal(expected, await this.grainDirectory.Lookup(expected.GrainId, CancellationToken.None));
        }

        [SkippableFact]
        public async Task LookupNotFound()
        {
            Assert.Null(await this.grainDirectory.Lookup(GrainId.Parse("user/somerandomuser_" + Guid.NewGuid().ToString("N")), CancellationToken.None));
        }
    }
}
