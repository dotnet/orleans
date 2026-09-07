using Microsoft.Extensions.DependencyInjection;
using CoreServiceCollectionExtensions = Orleans.Configuration.Internal.ServiceCollectionExtensions;
using Xunit;

namespace UnitTests.DependencyInjection;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddFromExisting_NullImplementation_ThrowsWithoutMutation()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Sentinel>();
        var existingDescriptor = Assert.Single(services);

        var exception = Assert.Throws<ArgumentNullException>(
            () => CoreServiceCollectionExtensions.AddFromExisting(services, typeof(ITestService), null!));

        Assert.Equal("implementation", exception.ParamName);
        Assert.Same(existingDescriptor, Assert.Single(services));
    }

    [Fact]
    public void AddFromExisting_NullService_ThrowsWithoutMutation()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TestService>();
        var existingDescriptor = Assert.Single(services);

        var exception = Assert.Throws<ArgumentNullException>(
            () => CoreServiceCollectionExtensions.AddFromExisting(services, null!, typeof(TestService)));

        Assert.Equal("service", exception.ParamName);
        Assert.Same(existingDescriptor, Assert.Single(services));
    }

    [Fact]
    public void AddFromExisting_RegisteredImplementation_PreservesLifetimeAndInstance()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TestService>();

        CoreServiceCollectionExtensions.AddFromExisting(services, typeof(ITestService), typeof(TestService));

        var alias = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ITestService));
        Assert.Equal(ServiceLifetime.Singleton, alias.Lifetime);

        using var provider = services.BuildServiceProvider();
        Assert.Same(provider.GetRequiredService<TestService>(), provider.GetRequiredService<ITestService>());
    }

    private sealed class Sentinel;

    private interface ITestService;

    private sealed class TestService : ITestService;
}
