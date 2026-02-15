#nullable enable
using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Xunit;

namespace NonSilo.Tests.Reminders;

[TestCategory("Reminders")]
public class SiloBuilderReminderExtensionsTests
{
    [Fact]
    public void AddReminders_RegistersLegacyReminderService()
    {
        var services = new ServiceCollection();

        services.AddReminders();

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(LocalReminderService));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(AdaptiveReminderService));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IReminderService));
    }

    [Fact]
    public void AddAdaptiveReminderService_RegistersAdaptiveServiceAndKeepsLegacyRegistration()
    {
        var services = new ServiceCollection();

        services.AddAdaptiveReminderService();

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(LocalReminderService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(AdaptiveReminderService));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IReminderService));
        Assert.Equal(2, services.Count(descriptor => descriptor.ServiceType == typeof(ILifecycleParticipant<ISiloLifecycle>)));
    }

    [Fact]
    public void AddAdaptiveReminderService_IsIdempotentForIReminderServiceBinding()
    {
        var services = new ServiceCollection();

        services.AddAdaptiveReminderService();
        services.AddAdaptiveReminderService();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(AdaptiveReminderService));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IReminderService));
    }

    [Fact]
    public void AddReminders_BuilderOverload_RegistersLegacyReminderService()
    {
        var builder = new TestSiloBuilder();

        builder.AddReminders();

        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(LocalReminderService));
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(AdaptiveReminderService));
        Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(IReminderService));
    }

    [Fact]
    public void AddAdaptiveReminderService_BuilderOverload_RegistersAdaptiveReminderService()
    {
        var builder = new TestSiloBuilder();

        builder.AddAdaptiveReminderService();

        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(AdaptiveReminderService));
        Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(IReminderService));
    }

    [Fact]
    public void AddAdaptiveReminderService_ConfigureOptions_UpdatesReminderOptions()
    {
        var services = new ServiceCollection();

        services.AddAdaptiveReminderService(options =>
        {
            options.LookAheadWindow = TimeSpan.FromSeconds(7);
            options.PollInterval = TimeSpan.FromSeconds(2);
            options.BaseBucketSize = 64;
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ReminderOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(7), options.LookAheadWindow);
        Assert.Equal(TimeSpan.FromSeconds(2), options.PollInterval);
        Assert.Equal(64u, options.BaseBucketSize);
    }

    [Fact]
    public void AddReminders_ConfigureOptions_UpdatesReminderOptions()
    {
        var services = new ServiceCollection();

        services.AddReminders(options =>
        {
            options.LookAheadWindow = TimeSpan.FromSeconds(9);
            options.MinimumReminderPeriod = TimeSpan.FromSeconds(3);
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ReminderOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(9), options.LookAheadWindow);
        Assert.Equal(TimeSpan.FromSeconds(3), options.MinimumReminderPeriod);
    }

    [Fact]
    public void AddReminders_BuilderOverloadWithConfigureOptions_UpdatesReminderOptions()
    {
        var builder = new TestSiloBuilder();

        builder.AddReminders(options => options.LookAheadWindow = TimeSpan.FromSeconds(11));

        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ReminderOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(11), options.LookAheadWindow);
    }

    [Fact]
    public void AddAdaptiveReminderService_BuilderOverloadWithConfigureOptions_UpdatesReminderOptions()
    {
        var builder = new TestSiloBuilder();

        builder.AddAdaptiveReminderService(options => options.LookAheadWindow = TimeSpan.FromSeconds(11));

        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ReminderOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(11), options.LookAheadWindow);
    }

    [Fact]
    public void AddAdaptiveReminderService_ConfigureOptions_DoesNotRewireLegacyBindings()
    {
        var services = new ServiceCollection();

        services.AddAdaptiveReminderService(options => options.EnableLegacyReminderService = true);

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IReminderService));
        Assert.Equal(2, services.Count(descriptor => descriptor.ServiceType == typeof(ILifecycleParticipant<ISiloLifecycle>)));
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
