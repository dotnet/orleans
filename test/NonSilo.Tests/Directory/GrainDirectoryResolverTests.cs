using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans;
using Orleans.GrainDirectory;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using TestExtensions;
using UnitTests.GrainInterfaces.Directories;
using UnitTests.Grains.Directories;
using Xunit;
using Xunit.Abstractions;

namespace NonSilo.Tests.Directory
{
    [TestCategory("BVT"), TestCategory("Directory")]
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
            hostBuilder.UseOrleans(siloBuilder =>
            {
                siloBuilder
                    .ConfigureServices(svc => svc.AddSingletonNamedService(CustomDirectoryGrain.DIRECTORY, (sp, nameof) => this.azureDirectory))
                    .ConfigureServices(svc => svc.AddSingletonNamedService("OtherDirectory", (sp, nameof) => this.otherDirectory))
                    .ConfigureServices(svc => svc.AddSingletonNamedService("AgainAnotherDirectory", (sp, nameof) => this.againAnotherDirectory))
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
    }
}
