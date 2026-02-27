using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Metadata;
using Orleans.Runtime;

#nullable enable
namespace Orleans.Runtime.ReminderService;

internal sealed class RegisterReminderActivationConfiguratorProvider : IConfigureGrainContextProvider
{
    private readonly ConcurrentDictionary<GrainType, RegisterReminderActivationConfigurator?> cache = new();
    private readonly Func<GrainType, Type?> grainTypeResolver;
    private readonly ILoggerFactory loggerFactory;

    public RegisterReminderActivationConfiguratorProvider(GrainClassMap grainClassMap, ILoggerFactory loggerFactory)
        : this(
            loggerFactory,
            grainType => grainClassMap.TryGetGrainClass(grainType, out var grainClass) ? grainClass : null)
    {
    }

    internal RegisterReminderActivationConfiguratorProvider(
        ILoggerFactory loggerFactory,
        Func<GrainType, Type?> grainTypeResolver)
    {
        this.loggerFactory = loggerFactory;
        this.grainTypeResolver = grainTypeResolver;
    }

    public bool TryGetConfigurator(GrainType grainType, GrainProperties properties, out IConfigureGrainContext configurator)
    {
        var result = cache.GetOrAdd(grainType, ResolveConfigurator);
        if (result is not null)
        {
            configurator = result;
            return true;
        }

        configurator = default!;
        return false;
    }

    private RegisterReminderActivationConfigurator? ResolveConfigurator(GrainType grainType)
    {
        var grainClass = grainTypeResolver(grainType);
        if (grainClass is null)
        {
            return null;
        }

        var registrations = grainClass
            .GetCustomAttributes(typeof(RegisterReminderAttribute), inherit: false)
            .Cast<RegisterReminderAttribute>()
            .ToArray();
        if (registrations.Length == 0)
        {
            return null;
        }

        var logger = loggerFactory.CreateLogger<RegisterReminderActivationLifecycleObserver>();
        if (!typeof(IRemindable).IsAssignableFrom(grainClass))
        {
            logger.LogWarning(
                "Ignoring [RegisterReminder] declarations for grain type {GrainType} because it does not implement {Remindable}.",
                grainClass.FullName,
                nameof(IRemindable));
            return null;
        }

        return new RegisterReminderActivationConfigurator(registrations, logger);
    }

    private sealed class RegisterReminderActivationConfigurator(
        RegisterReminderAttribute[] registrations,
        ILogger logger) : IConfigureGrainContext
    {
        public void Configure(IGrainContext context)
        {
            try
            {
                context.ObservableLifecycle.Subscribe(
                    observerName: nameof(RegisterReminderAttribute),
                    stage: GrainLifecycleStage.Activate,
                    observer: new RegisterReminderActivationLifecycleObserver(context, registrations, logger));
            }
            catch (NotSupportedException)
            {
                logger.LogWarning(
                    "Skipping [RegisterReminder] for grain {GrainId}: lifecycle hooks are not supported for this grain context.",
                    context.GrainId);
            }
            catch (NotImplementedException)
            {
                logger.LogWarning(
                    "Skipping [RegisterReminder] for grain {GrainId}: lifecycle hooks are not implemented for this grain context.",
                    context.GrainId);
            }
        }
    }

    private sealed class RegisterReminderActivationLifecycleObserver(
        IGrainContext grainContext,
        RegisterReminderAttribute[] registrations,
        ILogger logger) : ILifecycleObserver
    {
        public async Task OnStart(CancellationToken cancellationToken = default)
        {
            var reminderService = grainContext.ActivationServices.GetService<IReminderService>();
            if (reminderService is null)
            {
                logger.LogWarning(
                    "Skipping [RegisterReminder] activation registration for grain {GrainId} because {Service} is not configured.",
                    grainContext.GrainId,
                    nameof(IReminderService));
                return;
            }

            foreach (var registration in registrations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var existingReminder = await reminderService.GetReminder(grainContext.GrainId, registration.Name);
                    if (existingReminder is not null)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(registration.Cron))
                    {
                        await reminderService.RegisterOrUpdateReminder(
                            grainContext.GrainId,
                            registration.Name,
                            registration.Cron,
                            registration.Priority,
                            registration.Action);
                    }
                    else if (registration.Due is { } due && registration.Period is { } period)
                    {
                        await reminderService.RegisterOrUpdateReminder(
                            grainContext.GrainId,
                            registration.Name,
                            due,
                            period,
                            registration.Priority,
                            registration.Action);
                    }
                    else
                    {
                        logger.LogWarning(
                            "Skipping [RegisterReminder] for grain {GrainId}, reminder {ReminderName}: missing cron or due/period.",
                            grainContext.GrainId,
                            registration.Name);
                    }
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Failed [RegisterReminder] activation registration for grain {GrainId}, reminder {ReminderName}.",
                        grainContext.GrainId,
                        registration.Name);
                }
            }
        }

        public Task OnStop(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
