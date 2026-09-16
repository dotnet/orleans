#nullable enable
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Orleans.DurableJobs;
using Orleans.Metadata;
using Xunit;
using AdvancedReminderOptions = Orleans.AdvancedReminders.ReminderOptions;
using AttributeReminderServiceInterface = Orleans.AdvancedReminders.Runtime.ReminderService.IAttributeReminderService;
using IGrainReminder = Orleans.AdvancedReminders.IGrainReminder;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class RegisterReminderActivationConfiguratorProviderTests
{
    private static readonly GrainProperties EmptyGrainProperties = new(
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal, StringComparer.Ordinal));

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
    public async Task OnStart_ReconcilesAttributeReminderOnEveryActivation()
    {
        var provider = CreateProvider(typeof(ActivationIntervalRegistrationTestGrain));
        Assert.True(provider.TryGetConfigurator(GrainType.Create("test"), EmptyGrainProperties, out var configurator));

        var reminderService = Substitute.For<AttributeReminderServiceInterface>();
        reminderService.ReconcileReminder(
                Arg.Any<GrainId>(),
                Arg.Any<string>(),
                Arg.Any<ReminderSchedule>(),
                Arg.Any<MissedReminderAction>(),
                Arg.Any<string>())
            .Returns(Task.FromResult(Substitute.For<IGrainReminder>()));

        var (grainId, observer) = ConfigureAndCaptureObserver(configurator, reminderService);

        await observer.OnStart(CancellationToken.None);
        await observer.OnStart(CancellationToken.None);

        _ = reminderService.Received(2).ReconcileReminder(
            grainId,
            "interval-activation-registration",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Interval
                && schedule.DueTime == TimeSpan.FromSeconds(5)
                && schedule.Period == TimeSpan.FromSeconds(30)),
            MissedReminderAction.Skip,
            Arg.Is<string>(static declarationId => !string.IsNullOrWhiteSpace(declarationId)));
    }

    [Fact]
    public async Task OnStart_RegistersMissingIntervalReminder()
    {
        var provider = CreateProvider(typeof(ActivationIntervalRegistrationTestGrain));
        Assert.True(provider.TryGetConfigurator(GrainType.Create("test"), EmptyGrainProperties, out var configurator));

        var reminderService = Substitute.For<AttributeReminderServiceInterface>();
        reminderService.ReconcileReminder(
                Arg.Any<GrainId>(),
                Arg.Any<string>(),
                Arg.Any<ReminderSchedule>(),
                Arg.Any<MissedReminderAction>(),
                Arg.Any<string>())
            .Returns(Task.FromResult(Substitute.For<IGrainReminder>()));

        var (grainId, observer) = ConfigureAndCaptureObserver(configurator, reminderService);

        await observer.OnStart(CancellationToken.None);

        _ = reminderService.Received(1).ReconcileReminder(
            grainId,
            "interval-activation-registration",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Interval
                && schedule.DueTime == TimeSpan.FromSeconds(5)
                && schedule.DueAtUtc == null
                && schedule.Period == TimeSpan.FromSeconds(30)
                && schedule.CronExpression == null
                && schedule.CronTimeZoneId == null),
            MissedReminderAction.Skip,
            Arg.Is<string>(static declarationId => !string.IsNullOrWhiteSpace(declarationId)));
    }

    [Fact]
    public async Task OnStart_RegistersMissingCronReminder()
    {
        var provider = CreateProvider(typeof(ActivationCronRegistrationTestGrain));
        Assert.True(provider.TryGetConfigurator(GrainType.Create("test"), EmptyGrainProperties, out var configurator));

        var reminderService = Substitute.For<AttributeReminderServiceInterface>();
        reminderService.ReconcileReminder(
                Arg.Any<GrainId>(),
                Arg.Any<string>(),
                Arg.Any<ReminderSchedule>(),
                Arg.Any<MissedReminderAction>(),
                Arg.Any<string>())
            .Returns(Task.FromResult(Substitute.For<IGrainReminder>()));

        var (grainId, observer) = ConfigureAndCaptureObserver(configurator, reminderService);

        await observer.OnStart(CancellationToken.None);

        _ = reminderService.Received(1).ReconcileReminder(
            grainId,
            "cron-activation-registration",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Cron
                && schedule.CronExpression == "0 9 * * MON-FRI"
                && schedule.CronTimeZoneId == null
                && schedule.DueTime == null
                && schedule.DueAtUtc == null
                && schedule.Period == null),
            MissedReminderAction.FireImmediately,
            Arg.Is<string>(static declarationId => !string.IsNullOrWhiteSpace(declarationId)));
    }

    [Fact]
    public async Task OnStart_HandlesMissingReminderService()
    {
        var provider = CreateProvider(typeof(ActivationIntervalRegistrationTestGrain));
        Assert.True(provider.TryGetConfigurator(GrainType.Create("test"), EmptyGrainProperties, out var configurator));

        var (_, observer) = ConfigureAndCaptureObserver(configurator, reminderService: null);

        await observer.OnStart(CancellationToken.None);
    }

    [Fact]
    public async Task OnStart_EnforcesConfiguredMinimumPeriodThroughPublicReminderService()
    {
        var provider = CreateProvider(typeof(ActivationBelowMinimumRegistrationTestGrain));
        Assert.True(provider.TryGetConfigurator(GrainType.Create("test"), EmptyGrainProperties, out var configurator));
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(Arg.Any<GrainId>(), "below-minimum-activation-registration")
            .Returns(Task.FromResult<ReminderEntry?>(null));
        var service = new AdvancedReminderService(
            reminderTable,
            Substitute.For<ILocalDurableJobManager>(),
            Substitute.For<IGrainFactory>(),
            Options.Create(new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromMinutes(1) }),
            NullLogger<AdvancedReminderService>.Instance,
            TimeProvider.System,
            Substitute.For<IClusterManifestProvider>(),
            Substitute.For<IClusterMembershipService>());
        var (_, observer) = ConfigureAndCaptureObserver(configurator, service);

        await Assert.ThrowsAsync<ArgumentException>(() => observer.OnStart(CancellationToken.None));

        await reminderTable.DidNotReceive().UpsertRow(Arg.Any<ReminderEntry>());
    }

    private static RegisterReminderActivationConfiguratorProvider CreateProvider(Type grainType)
        => new(NullLoggerFactory.Instance, _ => grainType);

    private static (GrainId GrainId, ILifecycleObserver Observer) ConfigureAndCaptureObserver(
        IConfigureGrainContext configurator,
        AttributeReminderServiceInterface? reminderService)
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
