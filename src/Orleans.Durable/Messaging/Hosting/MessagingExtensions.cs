using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Durable.Messaging;
using Orleans.Durable.Messaging.Configuration;
using Orleans.Durable.Jobs;
using Orleans.Durable.Journaling.Json;

namespace Orleans.Hosting;

/// <summary>
/// Extensions for configuring messaging.
/// </summary>
public static class MessagingExtensions
{
    /// <summary>
    /// Adds inbox and outbox messaging support to the silo.
    /// </summary>
    /// <remarks>
    /// A grain receives messages by resolving <see cref="IInbox"/> during construction or activation setup, before journal recovery.
    /// </remarks>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The optional action used to configure inbox and outbox behavior.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddMessaging(this ISiloBuilder builder, Action<DurableMessagingOptions>? configureOptions = null)
    {
        builder.AddDurableJobs();
        return builder.ConfigureServices(services => services.AddMessaging(configureOptions));
    }

    /// <summary>
    /// Adds inbox and outbox messaging services.
    /// </summary>
    /// <remarks>
    /// A grain receives messages by resolving <see cref="IInbox"/> during construction or activation setup, before journal recovery.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The optional action used to configure inbox and outbox behavior.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddMessaging(this IServiceCollection services, Action<DurableMessagingOptions>? configureOptions = null)
    {
        services.AddDurableJobs();
        services.TryAddSingleton(TimeProvider.System);

        // The pump job is each grain's only durable wake-up for committed messaging work, so it retries indefinitely
        // while other jobs keep the configured policy.
        services.AddOptions<DurableJobsOptions>().PostConfigure<IServiceProvider>(static (options, services) =>
        {
            var shouldRetry = options.ShouldRetry;
            var clock = services.GetRequiredKeyedService<TimeProvider>(DurableJobTimeProviderNames.DurableJobs);
            var messaging = services.GetRequiredService<IOptions<DurableMessagingOptions>>();
            options.ShouldRetry = (context, exception) => string.Equals(context.Job.Name, MessagingPump.JobName, StringComparison.Ordinal)
                ? MessagingPump.GetRetryTime(context, clock.GetUtcNow(), messaging.Value.RetryInitialInterval)
                : shouldRetry(context, exception);
        });
        services.Configure<JsonJournalOptions>(options =>
        {
            options.AddTypeInfoResolver(InboxJsonContext.Default);
            options.AddTypeInfoResolver(OutboxJsonContext.Default);
        });

        var optionsBuilder = services.AddOptions<DurableMessagingOptions>();
        if (configureOptions is not null)
        {
            optionsBuilder.Configure(configureOptions);
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DurableMessagingOptions>, DurableMessagingOptionsValidation>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigurationValidator, DurableMessagingOptionsValidator>());

        services.ConfigureNamedOptionForLogging<DurableMessagingOptions>(Options.DefaultName);
        services.TryAddSingleton<DurableMessagingInstruments>();
        services.TryAddSingleton<MessagingShared>();
        services.TryAddSingleton<IOutboxTransport, GrainOutboxTransport>();
        services.TryAddScoped<MessagingPump>();
        services.TryAddScoped<Inbox>();
        services.Replace(ServiceDescriptor.Scoped<IInbox>(static sp => sp.GetRequiredService<Inbox>()));
        services.TryAddScoped<Outbox>();
        services.Replace(ServiceDescriptor.Scoped<IOutbox>(static sp => sp.GetRequiredService<Outbox>()));
        services.TryAddScoped<IMessagingDiagnostics, MessagingDiagnostics>();
        return services;
    }

    // Reports the invalid option's own message through the options contract.
    private sealed class DurableMessagingOptionsValidation : IValidateOptions<DurableMessagingOptions>
    {
        public ValidateOptionsResult Validate(string? name, DurableMessagingOptions options)
        {
            try
            {
                options.Validate();
                return ValidateOptionsResult.Success;
            }
            catch (ArgumentException exception)
            {
                return ValidateOptionsResult.Fail(exception.Message);
            }
        }
    }

    // Resolving the options runs their validation, so the silo reports invalid configuration before it starts.
    private sealed class DurableMessagingOptionsValidator(IOptions<DurableMessagingOptions> options) : IConfigurationValidator
    {
        public void ValidateConfiguration() => _ = options.Value;
    }
}
