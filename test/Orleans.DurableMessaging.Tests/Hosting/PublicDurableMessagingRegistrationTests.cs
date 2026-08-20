using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.DurableMessaging.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Hosting;

[TestCategory("BVT")]
public sealed class PublicDurableMessagingRegistrationTests
{
    [Fact]
    public void AddDurableMessaging_RegistersPublicScopedContractsAndInboxExtensionKey()
    {
        var services = new ServiceCollection();

        services.AddDurableMessaging();

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IDurableInbox));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IDurableOutbox));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IDurableMessagingDiagnostics));
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IGrainExtension)
                && Equals(descriptor.ServiceKey, typeof(IDurableInboxExtension)));
    }

    [Fact]
    public void AddDurableMessaging_PreservesCallerSuppliedTimeProviderAndAppliesOptions()
    {
        var expectedTime = new FixedTimeProvider(new DateTimeOffset(2040, 2, 3, 4, 5, 6, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(expectedTime);

        services.AddDurableMessaging(options =>
        {
            options.MaxCapacity = 17;
            options.InboxBatchSize = 4;
            options.OutboxBatchSize = 5;
        });
        using var provider = services.BuildServiceProvider();

        Assert.Same(expectedTime, provider.GetRequiredService<TimeProvider>());
        var configured = provider.GetRequiredService<IOptions<DurableInboxOptions>>().Value;
        Assert.Equal(17, configured.MaxCapacity);
        Assert.Equal(4, configured.InboxBatchSize);
        Assert.Equal(5, configured.OutboxBatchSize);
    }

    [Fact]
    public void AddDurableMessaging_InvalidOptionsFailThroughOptionsContract()
    {
        var services = new ServiceCollection();
        services.AddDurableMessaging(options => options.MaxCapacity = 0);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<DurableInboxOptions>>().Value);

        Assert.Contains("DurableInboxOptions validation failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalConsumerAssembly_HasNoFriendAccessToDurableMessaging()
    {
        var sourceAssembly = typeof(IDurableInbox).Assembly;
        var consumerName = typeof(PublicDurableMessagingRegistrationTests).Assembly.GetName().Name;
        var friendDeclarations = sourceAssembly
            .GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute")
            .Select(attribute => attribute.ConstructorArguments[0].Value?.ToString())
            .ToArray();

        Assert.DoesNotContain(friendDeclarations, declaration =>
            declaration?.StartsWith(consumerName!, StringComparison.Ordinal) == true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
