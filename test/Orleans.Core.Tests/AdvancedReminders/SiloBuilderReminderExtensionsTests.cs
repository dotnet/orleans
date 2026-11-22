#nullable enable
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.AdvancedReminders.Runtime.Hosting;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Orleans.AdvancedReminders.Timers;
using Orleans.Durable.Jobs;
using Xunit;
using AdvancedReminderOptions = Orleans.AdvancedReminders.ReminderOptions;
using AdvancedReminderServiceInterface = Orleans.AdvancedReminders.IReminderService;
using AttributeReminderServiceInterface = Orleans.AdvancedReminders.Runtime.ReminderService.IAttributeReminderService;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class SiloBuilderReminderExtensionsTests
{
    [Fact]
    public void HostConfiguration_AdvancedRemindersSection_ConfiguresProvider()
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Orleans:ClusterId"] = "test-cluster",
            ["Orleans:ServiceId"] = "test-service",
            ["Orleans:AdvancedReminders:ProviderType"] = "Memory",
        };
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(configuration))
            .UseOrleans(builder => builder.UseLocalhostClustering())
            .Build();

        Assert.IsType<InMemoryReminderTable>(
            host.Services.GetRequiredService<Orleans.AdvancedReminders.IReminderTable>());
    }

    [Fact]
    public void AddAdvancedReminders_RegistersAdvancedReminderService()
    {
        var services = new ServiceCollection();

        services.AddAdvancedReminders();

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(AdvancedReminderService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(AdvancedReminderServiceInterface));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(AttributeReminderServiceInterface));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IReminderRegistry));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IConfigurationValidator));
    }

    [Fact]
    public void AddAdvancedReminders_IsIdempotentForAdvancedReminderServiceBinding()
    {
        var services = new ServiceCollection();

        services.AddAdvancedReminders();
        services.AddAdvancedReminders();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(AdvancedReminderService));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(AdvancedReminderServiceInterface));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(AttributeReminderServiceInterface));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IReminderRegistry));
    }

    [Fact]
    public void AddAdvancedReminders_BuilderOverload_RegistersAdvancedReminderService()
    {
        var builder = new TestSiloBuilder();

        builder.AddAdvancedReminders();

        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(AdvancedReminderService));
        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(AdvancedReminderServiceInterface));
        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(AttributeReminderServiceInterface));
    }

    [Fact]
    public void AddAdvancedReminders_ConfigureOptions_UpdatesReminderOptions()
    {
        var services = new ServiceCollection();

        services.AddAdvancedReminders(options =>
        {
            options.MissedReminderGracePeriod = TimeSpan.FromSeconds(9);
            options.MinimumReminderPeriod = TimeSpan.FromSeconds(3);
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AdvancedReminderOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(9), options.MissedReminderGracePeriod);
        Assert.Equal(TimeSpan.FromSeconds(3), options.MinimumReminderPeriod);
    }

    [Fact]
    public void AddAdvancedReminders_BuilderOverloadWithConfigureOptions_UpdatesReminderOptions()
    {
        var builder = new TestSiloBuilder();

        builder.AddAdvancedReminders(options => options.MissedReminderGracePeriod = TimeSpan.FromSeconds(11));

        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AdvancedReminderOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(11), options.MissedReminderGracePeriod);
    }

    [Fact]
    public void UseInMemoryAdvancedReminderService_RegistersInMemoryReminderTable()
    {
        var builder = new TestSiloBuilder();

        builder.UseInMemoryAdvancedReminderService();

        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(InMemoryReminderTable));
        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(Orleans.AdvancedReminders.IReminderTable));
    }

    [Fact]
    public void AddAdvancedReminders_WithoutDurableJobsBackend_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAdvancedReminders();

        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OrleansConfigurationException>(() =>
        {
            foreach (var validator in provider.GetServices<IConfigurationValidator>())
            {
                validator.ValidateConfiguration();
            }
        });

        Assert.Contains("DurableJobs requires Orleans.Durable.Journaling storage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAdvancedReminders_WithoutReminderTable_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAdvancedReminders();
        services.UseInMemoryDurableJobs();

        using var provider = services.BuildServiceProvider();
        var validator = provider.GetServices<IConfigurationValidator>()
            .OfType<AdvancedReminderJobBackendValidator>()
            .Single();

        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);

        Assert.Contains("UseInMemoryAdvancedReminderService()", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseInMemoryAdvancedReminderService_RegistersDurableJobsBackend()
    {
        var builder = new TestSiloBuilder();

        builder.UseInMemoryAdvancedReminderService();

        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(JobShardManager));
    }

    [Fact]
    public void AddAdvancedReminders_WithShortDurableJobsActivationBuffer_PassesValidation()
    {
        var builder = new TestSiloBuilder();
        builder.Services.AddLogging();
        builder.UseInMemoryAdvancedReminderService();
        builder.Services.Configure<DurableJobsOptions>(options =>
            options.ShardActivationBufferPeriod = TimeSpan.FromSeconds(1));
        using var provider = builder.Services.BuildServiceProvider();
        var validator = provider.GetServices<IConfigurationValidator>()
            .OfType<AdvancedReminderJobBackendValidator>()
            .Single();

        validator.ValidateConfiguration();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddAdvancedReminders_FallsBackOnlyForRetainedReminderJobs(bool untilSuccess)
    {
        var clock = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<TimeProvider>(DurableJobTimeProviderNames.DurableJobs, clock);
        var retryDelay = TimeSpan.FromSeconds(17);
        services.AddAdvancedReminders(options =>
        {
            options.DeliveryRetryPolicy = untilSuccess
                ? Orleans.AdvancedReminders.ReminderRetryPolicy.UntilSuccess
                : Orleans.AdvancedReminders.ReminderRetryPolicy.Limited(3);
            options.MinimumReminderPeriod = retryDelay;
        });
        DateTimeOffset? configuredRetry = null;
        services.Configure<DurableJobsOptions>(options => options.ShouldRetry = (_, _) => configuredRetry);

        using var provider = services.BuildServiceProvider();
        var shouldRetry = provider.GetRequiredService<IOptions<DurableJobsOptions>>().Value.ShouldRetry;
        var context = Substitute.For<IJobRunContext>();
        var recipient = GrainId.Create("test", "recipient");
        var dispatcher = GrainId.Create(AdvancedReminderDispatcherGrain.GrainTypeName, recipient.ToString());
        var failure = new InvalidOperationException("Injected dispatcher failure.");
        context.Job.Returns(CreateJob(dispatcher));
        Assert.Equal(untilSuccess ? clock.GetUtcNow() + retryDelay : null, shouldRetry(context, failure));

        configuredRetry = clock.GetUtcNow().AddSeconds(23);
        Assert.Equal(configuredRetry, shouldRetry(context, failure));

        configuredRetry = null;
        context.Job.Returns(CreateJob(GrainId.Create("otherjob", recipient.ToString())));
        Assert.Null(shouldRetry(context, failure));
        context.Job.Returns(CreateJob(GrainId.Create(AdvancedReminderDispatcherGrain.GrainTypeName, "different-recipient")));
        Assert.Null(shouldRetry(context, failure));

        DurableJob CreateJob(GrainId target) => new()
        {
            Id = "job",
            Name = "advanced-reminder:test",
            ShardId = "test-shard",
            TargetGrainId = target,
            DueTime = clock.GetUtcNow(),
            Metadata = new Dictionary<string, string> { ["grain-id"] = recipient.ToString(), ["reminder-name"] = "test" },
        };
    }

    private sealed class TestSiloBuilder(IServiceCollection? services = null) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = services ?? new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
