using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Orleans.GrainDirectory;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Hosting;
using Xunit;

namespace NonSilo.Tests.Directory;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("GrainDirectory")]
public sealed class DirectorySiloBuilderExtensionsTests
{
    [Fact]
    public void AddGrainDirectoryRejectsNullSiloBuilder()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => DirectorySiloBuilderExtensions.AddGrainDirectory<IGrainDirectory>(
                (ISiloBuilder)null!, "test", (_, _) => Substitute.For<IGrainDirectory>()));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void AddGrainDirectoryRejectsNullServiceCollection()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => DirectorySiloBuilderExtensions.AddGrainDirectory<IGrainDirectory>(
                (IServiceCollection)null!, "test", (_, _) => Substitute.For<IGrainDirectory>()));

        Assert.Equal("collection", exception.ParamName);
    }

    [Fact]
    public void NullFactoryPreservesExistingDefaultDirectory()
    {
        var services = new ServiceCollection();
        var directory = Substitute.For<IGrainDirectory>();
        services.AddGrainDirectory<IGrainDirectory>(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, (_, _) => directory);
        var registrations = services.ToArray();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            services.AddGrainDirectory<IGrainDirectory>(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, null!));

        Assert.Equal("implementationFactory", exception.ParamName);
        Assert.Equal(registrations, services);
        using var provider = services.BuildServiceProvider();
        Assert.Same(directory, provider.GetRequiredKeyedService<IGrainDirectory>(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY));
    }

    [Fact]
    public void NamedRegistrationPassesProviderAndNameToFactoryOnce()
    {
        var services = new ServiceCollection();
        var directory = Substitute.For<IGrainDirectory>();
        IServiceProvider factoryProvider = null!;
        string factoryName = null!;
        var calls = 0;

        var result = services.AddGrainDirectory<IGrainDirectory>("custom", (provider, name) =>
        {
            factoryProvider = provider;
            factoryName = name;
            calls++;
            return directory;
        });
        using var provider = services.BuildServiceProvider();

        Assert.Same(services, result);
        Assert.Same(directory, provider.GetRequiredKeyedService<IGrainDirectory>("custom"));
        Assert.Same(directory, provider.GetRequiredKeyedService<IGrainDirectory>("custom"));
        Assert.Same(provider.GetRequiredService<IServiceProvider>(), factoryProvider);
        Assert.Equal("custom", factoryName);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void SiloRegistrationPreservesBuilderAndParticipatesInLifecycle()
    {
        var directory = Substitute.For<IGrainDirectory, ILifecycleParticipant<ISiloLifecycle>>();
        using var host = new HostBuilder().UseOrleans(builder =>
        {
            builder.UseLocalhostClustering();

            var result = builder.AddGrainDirectory<IGrainDirectory>("custom", (_, _) => directory);

            Assert.Same(builder, result);
        }).Build();

        Assert.Same(directory, host.Services.GetRequiredKeyedService<IGrainDirectory>("custom"));
        Assert.Contains(host.Services.GetServices<ILifecycleParticipant<ISiloLifecycle>>(),
            participant => ReferenceEquals(participant, directory));
    }
}
