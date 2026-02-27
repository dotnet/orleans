#nullable enable
using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Xunit;

namespace NonSilo.Tests.Reminders;

internal interface IActivationIntervalRegistrationTestGrain : IGrainWithGuidKey
{
}

[RegisterReminder("interval-activation-registration", dueSeconds: 5, periodSeconds: 30)]
internal sealed class ActivationIntervalRegistrationTestGrain : Grain, IActivationIntervalRegistrationTestGrain, IRemindable
{
    public Task ReceiveReminder(string reminderName, TickStatus status) => Task.CompletedTask;
}

internal interface IActivationCronRegistrationTestGrain : IGrainWithGuidKey
{
}

[RegisterReminder(
    "cron-activation-registration",
    "0 9 * * MON-FRI",
    priority: ReminderPriority.High,
    action: MissedReminderAction.FireImmediately)]
internal sealed class ActivationCronRegistrationTestGrain : Grain, IActivationCronRegistrationTestGrain, IRemindable
{
    public Task ReceiveReminder(string reminderName, TickStatus status) => Task.CompletedTask;
}

internal interface IActivationNoAttributeTestGrain : IGrainWithGuidKey
{
}

internal sealed class ActivationNoAttributeTestGrain : Grain, IActivationNoAttributeTestGrain, IRemindable
{
    public Task ReceiveReminder(string reminderName, TickStatus status) => Task.CompletedTask;
}

internal interface IActivationNonRemindableTestGrain : IGrainWithGuidKey
{
}

[RegisterReminder("non-remindable", dueSeconds: 1, periodSeconds: 5)]
internal sealed class ActivationNonRemindableTestGrain : Grain, IActivationNonRemindableTestGrain
{
}

[TestCategory("Reminders")]
public class RegisterReminderActivationConfiguratorProviderTests
{
    private static readonly GrainProperties EmptyGrainProperties = new(ImmutableDictionary<string, string>.Empty);

    [Fact]
    public void TryGetConfigurator_ReturnsFalse_WhenNoRegisterReminderAttribute()
    {
        var provider = CreateProvider(typeof(ActivationNoAttributeTestGrain));

        var found = provider.TryGetConfigurator(GrainType.Create("test"), EmptyGrainProperties, out _);

        Assert.False(found);
    }

    [Fact]
    public void TryGetConfigurator_ReturnsFalse_WhenGrainIsNotRemindable()
    {
        var provider = CreateProvider(typeof(ActivationNonRemindableTestGrain));

        var found = provider.TryGetConfigurator(GrainType.Create("test"), EmptyGrainProperties, out _);

        Assert.False(found);
    }

    [Fact]
    public async Task OnStart_DoesNotOverwriteExistingReminder()
    {
        var provider = CreateProvider(typeof(ActivationIntervalRegistrationTestGrain));
        Assert.True(provider.TryGetConfigurator(GrainType.Create("test"), EmptyGrainProperties, out var configurator));

        var reminderService = Substitute.For<IReminderService>();
        var existingReminder = Substitute.For<IGrainReminder>();
        reminderService.GetReminder(Arg.Any<GrainId>(), "interval-activation-registration")
            .Returns(Task.FromResult(existingReminder));

        var (grainId, observer) = ConfigureAndCaptureObserver(configurator, reminderService);

        await observer.OnStart(CancellationToken.None);

        _ = reminderService.DidNotReceive().RegisterOrUpdateReminder(
            grainId,
            "interval-activation-registration",
            Arg.Any<TimeSpan>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<ReminderPriority>(),
            Arg.Any<MissedReminderAction>());
    }

    [Fact]
    public async Task OnStart_RegistersMissingIntervalReminder()
    {
        var provider = CreateProvider(typeof(ActivationIntervalRegistrationTestGrain));
        Assert.True(provider.TryGetConfigurator(GrainType.Create("test"), EmptyGrainProperties, out var configurator));

        var reminderService = Substitute.For<IReminderService>();
        reminderService.GetReminder(Arg.Any<GrainId>(), "interval-activation-registration")
            .Returns(Task.FromResult<IGrainReminder>(null!));
        reminderService.RegisterOrUpdateReminder(
                Arg.Any<GrainId>(),
                Arg.Any<string>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<ReminderPriority>(),
                Arg.Any<MissedReminderAction>())
            .Returns(Task.FromResult(Substitute.For<IGrainReminder>()));

        var (grainId, observer) = ConfigureAndCaptureObserver(configurator, reminderService);

        await observer.OnStart(CancellationToken.None);

        _ = reminderService.Received(1).RegisterOrUpdateReminder(
            grainId,
            "interval-activation-registration",
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            ReminderPriority.Normal,
            MissedReminderAction.Skip);
    }

    [Fact]
    public async Task OnStart_RegistersMissingCronReminder()
    {
        var provider = CreateProvider(typeof(ActivationCronRegistrationTestGrain));
        Assert.True(provider.TryGetConfigurator(GrainType.Create("test"), EmptyGrainProperties, out var configurator));

        var reminderService = Substitute.For<IReminderService>();
        reminderService.GetReminder(Arg.Any<GrainId>(), "cron-activation-registration")
            .Returns(Task.FromResult<IGrainReminder>(null!));
        reminderService.RegisterOrUpdateReminder(
                Arg.Any<GrainId>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<ReminderPriority>(),
                Arg.Any<MissedReminderAction>())
            .Returns(Task.FromResult(Substitute.For<IGrainReminder>()));

        var (grainId, observer) = ConfigureAndCaptureObserver(configurator, reminderService);

        await observer.OnStart(CancellationToken.None);

        _ = reminderService.Received(1).RegisterOrUpdateReminder(
            grainId,
            "cron-activation-registration",
            "0 9 * * MON-FRI",
            ReminderPriority.High,
            MissedReminderAction.FireImmediately);
    }

    [Fact]
    public async Task OnStart_HandlesMissingReminderService()
    {
        var provider = CreateProvider(typeof(ActivationIntervalRegistrationTestGrain));
        Assert.True(provider.TryGetConfigurator(GrainType.Create("test"), EmptyGrainProperties, out var configurator));

        var (_, observer) = ConfigureAndCaptureObserver(configurator, reminderService: null);

        await observer.OnStart(CancellationToken.None);
    }

    private static RegisterReminderActivationConfiguratorProvider CreateProvider(Type grainType)
        => new(NullLoggerFactory.Instance, _ => grainType);

    private static (GrainId GrainId, ILifecycleObserver Observer) ConfigureAndCaptureObserver(
        IConfigureGrainContext configurator,
        IReminderService? reminderService)
    {
        ILifecycleObserver? observer = null;
        var lifecycle = Substitute.For<IGrainLifecycle>();
        lifecycle.Subscribe(Arg.Any<string>(), GrainLifecycleStage.Activate, Arg.Any<ILifecycleObserver>())
            .Returns(callInfo =>
            {
                observer = callInfo.ArgAt<ILifecycleObserver>(2);
                return Substitute.For<IDisposable>();
            });

        var grainId = GrainId.Create("test", "activation-registration");
        var services = new ServiceCollection();
        if (reminderService is not null)
        {
            services.AddSingleton(reminderService);
        }

        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(grainId);
        context.ObservableLifecycle.Returns(lifecycle);
        context.ActivationServices.Returns(services.BuildServiceProvider());

        configurator.Configure(context);

        Assert.NotNull(observer);
        return (grainId, observer!);
    }
}
