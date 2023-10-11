using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestPlatform.Utilities;
using NSubstitute;
using Orleans.GrainDirectory;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using TestExtensions;
using UnitTests.Grains.Directories;
using Xunit;
using Xunit.Abstractions;

namespace NonSilo.Tests.Directory
{
    [TestCategory("BVT"), TestCategory("Directory")]
    public class GrainLocatorResolverTests
    {
        private readonly IGrainDirectory customDirectory;
        private readonly IHost host;
        private readonly GrainLocatorResolver target;

        public GrainLocatorResolverTests(ITestOutputHelper output)
        {
            this.customDirectory = Substitute.For<IGrainDirectory>();

            var hostBuilder = new HostBuilder();
            hostBuilder.UseOrleans((ctx, siloBuilder) =>
            {
                siloBuilder
                    .ConfigureServices(svc => svc.AddSingleton(Substitute.For<DhtGrainLocator>(null, null)))
                    .ConfigureServices(svc => svc.AddSingletonNamedService(CustomDirectoryGrain.DIRECTORY, (sp, nameof) => this.customDirectory))
                    .ConfigureLogging(builder => builder.AddProvider(new XunitLoggerProvider(output)))
                    .UseLocalhostClustering();
            });
            this.host = hostBuilder.Build();

            this.target = this.host.Services.GetRequiredService<GrainLocatorResolver>();
        }

        [Fact]
        public void ReturnsDhtGrainLocatorWhenUsingDhtDirectory()
        {
            var grainLocator = this.host.Services.GetRequiredService<DhtGrainLocator>();
            Assert.Same(grainLocator, target.GetGrainLocator(GrainType.Create(DefaultDirectoryGrain.DIRECTORY)));
        }

        [Fact]
        public void ReturnsCachedGrainLocatorWhenUsingCustomDirectory()
        {
            var grainLocator = this.host.Services.GetRequiredService<CachedGrainLocator>();
            Assert.Same(grainLocator, target.GetGrainLocator(GrainType.Create(CustomDirectoryGrain.DIRECTORY)));
        }

        [Fact]
        public void ReturnsClientGrainLocatorWhenUsingClient()
        {
            var grainLocator = this.host.Services.GetRequiredService<ClientGrainLocator>();
            Assert.Same(grainLocator, target.GetGrainLocator(ClientGrainId.Create("client").GrainId.Type));
        }
    }
}