using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans;
using Orleans.GrainDirectory;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Hosting;
using Orleans.Runtime.GrainDirectory;
using TestExtensions;
using UnitTests.GrainInterfaces.Directories;
using UnitTests.Grains.Directories;
using Xunit;

namespace NonSilo.Tests.Directory
{
    /// <summary>
    /// Tests for grain directory resolver functionality including custom directory resolution and directory listing.
    /// </summary>
    [TestCategory("BVT"), TestCategory("Directory")]
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("GrainDirectory")]
    public class GrainDirectoryResolverTests
    {
        private readonly IGrainDirectory azureDirectory = Substitute.For<IGrainDirectory>();
        private readonly IGrainDirectory otherDirectory = Substitute.For<IGrainDirectory>();
        private readonly IGrainDirectory againAnotherDirectory = Substitute.For<IGrainDirectory>();
        private readonly IHost host;
        private readonly GrainDirectoryResolver target;

        public GrainDirectoryResolverTests(ITestOutputHelper output)
        {
            this.azureDirectory = Substitute.For<IGrainDirectory>();

            var hostBuilder = new HostBuilder();
            hostBuilder.UseOrleans((ctx, siloBuilder) =>
            {
                siloBuilder
                    .UseLocalGrainDirectory()
                    .AddGrainDirectory(CustomDirectoryGrain.DIRECTORY, (sp, nameof) => this.azureDirectory)
                    .AddGrainDirectory("OtherDirectory", (sp, nameof) => this.otherDirectory)
                    .AddGrainDirectory("AgainAnotherDirectory", (sp, nameof) => this.againAnotherDirectory)
                    .ConfigureLogging(builder => builder.AddProvider(new XunitLoggerProvider(output)))
                    .UseLocalhostClustering();
            });

            this.host = hostBuilder.Build();

            this.target = host.Services.GetRequiredService<GrainDirectoryResolver>();
        }

        [Fact]
        public void UserProvidedDirectory()
        {
            var grainId = host.Services.GetRequiredService<IGrainFactory>().GetGrain<ICustomDirectoryGrain>(Guid.NewGuid()).GetGrainId();
            Assert.Same(this.azureDirectory, this.target.Resolve(grainId.Type));
        }

        [Fact]
        public void DefaultDhtDirectory()
        {
            Assert.Null(this.target.Resolve(GrainType.Create(DefaultDirectoryGrain.DIRECTORY)));
        }

        [Fact]
        public void ListAllDirectories()
        {

            var expected = new[] { this.azureDirectory, this.otherDirectory, this.againAnotherDirectory };
            Assert.Equal(expected, this.target.Directories.ToArray());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RuntimeDefaultUsesDistributedDirectory(bool addNamedAlias)
        {
            using var configuredHost = new HostBuilder().UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering();
                if (addNamedAlias)
                {
                    siloBuilder.AddDistributedGrainDirectory("named");
                }
            }).Build();

            var services = configuredHost.Services;
            var resolver = services.GetRequiredService<GrainDirectoryResolver>();
            var directory = Assert.IsType<DistributedGrainDirectory>(resolver.DefaultGrainDirectory);
            Assert.Same(directory, resolver.Resolve(GrainType.Create(DefaultDirectoryGrain.DIRECTORY)));
            Assert.Same(directory, Assert.Single(resolver.Directories));
            Assert.Same(
                services.GetRequiredService<CachedGrainLocator>(),
                services.GetRequiredService<GrainLocatorResolver>().GetGrainLocator(GrainType.Create(DefaultDirectoryGrain.DIRECTORY)));
            if (addNamedAlias)
            {
                Assert.Same(directory, services.GetRequiredKeyedService<IGrainDirectory>("named"));
            }

            Assert.Same(
                directory,
                Assert.Single(
                    services.GetServices<ILifecycleParticipant<ISiloLifecycle>>(),
                    static participant => participant is DistributedGrainDirectory));
        }

        [Fact]
        public void LocalDirectorySelectionRemovesDistributedDefault()
        {
            using var configuredHost = new HostBuilder().UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering();
                siloBuilder.AddDistributedGrainDirectory();
                siloBuilder.UseLocalGrainDirectory();
                siloBuilder.UseLocalGrainDirectory();
            }).Build();

            var services = configuredHost.Services;
            var resolver = services.GetRequiredService<GrainDirectoryResolver>();
            Assert.Null(resolver.DefaultGrainDirectory);
            Assert.Null(resolver.Resolve(GrainType.Create(DefaultDirectoryGrain.DIRECTORY)));
            Assert.Empty(resolver.Directories);
            Assert.Null(services.GetService<DistributedGrainDirectory>());
            Assert.Null(services.GetService<DirectoryMembershipService>());
            Assert.DoesNotContain(
                services.GetServices<ILifecycleParticipant<ISiloLifecycle>>(),
                static participant => participant is DistributedGrainDirectory);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LocalDirectorySelectionPreservesNamedDistributedDirectory(bool registerNamedFirst)
        {
            using var configuredHost = new HostBuilder().UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering();
                siloBuilder.AddDistributedGrainDirectory();
                if (registerNamedFirst)
                {
                    siloBuilder.AddDistributedGrainDirectory("named");
                }

                siloBuilder.UseLocalGrainDirectory();
                if (!registerNamedFirst)
                {
                    siloBuilder.AddDistributedGrainDirectory("named");
                }
            }).Build();

            var services = configuredHost.Services;
            var resolver = services.GetRequiredService<GrainDirectoryResolver>();
            var named = services.GetRequiredKeyedService<IGrainDirectory>("named");
            Assert.IsType<DistributedGrainDirectory>(named);
            Assert.Null(resolver.DefaultGrainDirectory);
            Assert.Null(resolver.Resolve(GrainType.Create(DefaultDirectoryGrain.DIRECTORY)));
            Assert.Same(named, Assert.Single(resolver.Directories));
            Assert.Same(
                named,
                Assert.Single(
                    services.GetServices<ILifecycleParticipant<ISiloLifecycle>>(),
                    static participant => participant is DistributedGrainDirectory));
        }

        [Fact]
        public void DistributedDirectoryCanBeSelectedAfterLocalDirectory()
        {
            using var configuredHost = new HostBuilder().UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering();
                siloBuilder.AddDistributedGrainDirectory();
                siloBuilder.UseLocalGrainDirectory();
                siloBuilder.AddDistributedGrainDirectory("named");
                siloBuilder.AddDistributedGrainDirectory();
                siloBuilder.AddDistributedGrainDirectory();
            }).Build();

            var services = configuredHost.Services;
            var resolver = services.GetRequiredService<GrainDirectoryResolver>();
            var directory = Assert.IsType<DistributedGrainDirectory>(resolver.DefaultGrainDirectory);
            Assert.Same(directory, resolver.Resolve(GrainType.Create(DefaultDirectoryGrain.DIRECTORY)));
            Assert.Same(directory, services.GetRequiredKeyedService<IGrainDirectory>("named"));
            Assert.Same(directory, Assert.Single(resolver.Directories));
            Assert.Same(
                directory,
                Assert.Single(
                    services.GetServices<ILifecycleParticipant<ISiloLifecycle>>(),
                    static participant => participant is DistributedGrainDirectory));
        }

        [Fact]
        public void CustomDefaultReplacesDistributedDirectory()
        {
            using var configuredHost = new HostBuilder().UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering();
                siloBuilder.AddDistributedGrainDirectory();
                siloBuilder.AddGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, (_, _) => this.otherDirectory);
            }).Build();

            var services = configuredHost.Services;
            var resolver = services.GetRequiredService<GrainDirectoryResolver>();
            Assert.Same(this.otherDirectory, resolver.DefaultGrainDirectory);
            Assert.Same(this.otherDirectory, Assert.Single(resolver.Directories));
            Assert.Null(services.GetService<DistributedGrainDirectory>());
            Assert.Null(services.GetService<DirectoryMembershipService>());
        }

        [Fact]
        public void LocalDirectorySelectionRemovesCustomDefault()
        {
            using var configuredHost = new HostBuilder().UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering();
                siloBuilder.AddGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, (_, _) => this.otherDirectory);
                siloBuilder.UseLocalGrainDirectory();
            }).Build();

            var services = configuredHost.Services;
            var resolver = services.GetRequiredService<GrainDirectoryResolver>();
            Assert.Null(resolver.DefaultGrainDirectory);
            Assert.Empty(resolver.Directories);
            Assert.Null(services.GetKeyedService<IGrainDirectory>(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY));
        }

        [Fact]
        public void DistributedSelectionReplacesCustomDefaultLifecycle()
        {
            using var configuredHost = new HostBuilder().UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering();
                siloBuilder.AddGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, (_, _) => this.otherDirectory);
                siloBuilder.AddDistributedGrainDirectory();
            }).Build();

            var services = configuredHost.Services;
            var directory = Assert.IsType<DistributedGrainDirectory>(
                services.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory);
            Assert.Same(
                directory,
                Assert.Single(
                    services.GetServices<ILifecycleParticipant<ISiloLifecycle>>(),
                    static participant => participant is DistributedGrainDirectory));
        }
    }
}
