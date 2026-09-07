using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Xunit;

namespace UnitTests.DependencyInjection;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public class NamedServiceConfiguratorTests
{
    [Fact]
    public void ConfigureComponent_NullFactory_ThrowsBeforeConfiguration()
    {
        var configurator = new TrackingNamedServiceConfigurator();

        var exception = Assert.Throws<ArgumentNullException>(
            () => configurator.ConfigureComponent<TestOptions, TestComponent>(null!));

        Assert.Equal("factory", exception.ParamName);
        Assert.Equal(0, configurator.ConfigureInvocationCount);
    }

    [Fact]
    public void ConfigureComponent_ValidName_RegistersNamedOptionsAndKeyedSingleton()
    {
        const string Name = "alpha";
        var services = new ServiceCollection();
        var factoryNames = new List<string>();
        var configurator = new NamedServiceConfigurator(Name, configure => configure(services));

        configurator.ConfigureComponent<TestOptions, TestComponent>(
            (_, name) =>
            {
                factoryNames.Add(name);
                return new TestComponent(name);
            },
            builder => builder.Configure(options => options.Value = 42));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<TestOptions>>().Get(Name);
        var first = provider.GetRequiredKeyedService<TestComponent>(Name);
        var second = provider.GetRequiredKeyedService<TestComponent>(Name);

        Assert.Equal(42, options.Value);
        Assert.Equal(Name, first.Name);
        Assert.Same(first, second);
        Assert.Equal([Name], factoryNames);
    }

    private sealed class TrackingNamedServiceConfigurator : INamedServiceConfigurator
    {
        public string Name => "tracking";

        public int ConfigureInvocationCount { get; private set; }

        public Action<Action<IServiceCollection>> ConfigureDelegate => configure =>
        {
            ConfigureInvocationCount++;
            configure(new ServiceCollection());
        };
    }

    private sealed class TestOptions
    {
        public int Value { get; set; }
    }

    private sealed class TestComponent(string name)
    {
        public string Name { get; } = name;
    }
}
