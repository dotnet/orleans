using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans;
using Orleans.ApplicationParts;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Serialization;
using Tester.HostBuilder.Fakes;
using TestExtensions;
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
        private readonly IGrainDirectoryResolver target;

        public GrainDirectoryResolverTests(ITestOutputHelper output)
        {
            this.azureDirectory = Substitute.For<IGrainDirectory>();

            var hostBuilder = new HostBuilder();
            hostBuilder.UseOrleans(siloBuilder =>
            {
                siloBuilder
                    .ConfigureServices((ctx, svc) => svc.AddSingletonNamedService(AzureTableDirectoryGrain.DIRECTORY, (sp, nameof) => this.azureDirectory))
                    .ConfigureServices((ctx, svc) => svc.AddSingletonNamedService("OtherDirectory", (sp, nameof) => this.otherDirectory))
                    .ConfigureServices((ctx, svc) => svc.AddSingletonNamedService("AgainAnotherDirectory", (sp, nameof) => this.againAnotherDirectory))
                    .ConfigureLogging(builder => builder.AddProvider(new XunitLoggerProvider(output)))
                    .UseLocalhostClustering();
            });

            var host = hostBuilder.Build();

            this.target = host.Services.GetRequiredService<IGrainDirectoryResolver>();
        }

        [Fact]
        public void UserProvidedDirectory()
        {
            var grainId = LegacyGrainId.GetGrainId(AzureTableDirectoryGrain.TYPECODE, Guid.NewGuid());
            Assert.Same(this.azureDirectory, this.target.Resolve(grainId));
        }

        [Fact]
        public void DefaultDhtDirectory()
        {
            var grainId = LegacyGrainId.GetGrainId(DefaultDirectoryGrain.TYPECODE, Guid.NewGuid());
            Assert.Null(this.target.Resolve(grainId));
        }

        [Fact]
        public void ListAllDirectories()
        {
            
            var expected = new[] { this.azureDirectory, this.otherDirectory, this.againAnotherDirectory };
            Assert.Equal(expected, this.target.Directories.ToArray());
        }
    }
}
