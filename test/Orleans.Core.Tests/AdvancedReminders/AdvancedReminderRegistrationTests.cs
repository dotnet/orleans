#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Cron.Internal;
using Orleans.AdvancedReminders.Runtime;
using Orleans.AdvancedReminders.Runtime.Hosting;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Orleans.AdvancedReminders.Timers;
using Orleans.Configuration.Internal;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Runtime;
using Xunit;
using AdvancedRemindable = Orleans.AdvancedReminders.IRemindable;
using AdvancedReminderException = Orleans.AdvancedReminders.Runtime.ReminderException;
using AdvancedReminderOptions = Orleans.AdvancedReminders.ReminderOptions;
using AdvancedReminderOptionsValidator = Orleans.AdvancedReminders.ReminderOptionsValidator;
using AdvancedReminderServiceInterface = Orleans.AdvancedReminders.IReminderService;
using AdvancedTickStatus = Orleans.AdvancedReminders.Runtime.TickStatus;
using AttributeReminderServiceInterface = Orleans.AdvancedReminders.Runtime.ReminderService.IAttributeReminderService;
using IGrainReminder = Orleans.AdvancedReminders.IGrainReminder;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using ReminderTableData = Orleans.AdvancedReminders.ReminderTableData;

namespace UnitTests.AdvancedReminders;

internal interface IActivationIntervalRegistrationTestGrain : IGrainWithGuidKey;

[RegisterReminder("interval-activation-registration", dueSeconds: 5, periodSeconds: 30)]
internal sealed class ActivationIntervalRegistrationTestGrain : Grain, IActivationIntervalRegistrationTestGrain, AdvancedRemindable
{
    public Task ReceiveReminder(string reminderName, AdvancedTickStatus status) => Task.CompletedTask;
}

internal interface IActivationCronRegistrationTestGrain : IGrainWithGuidKey;

[RegisterReminder(
    "cron-activation-registration",
    "0 9 * * MON-FRI",
    priority: DurableJobPriority.High,
    action: MissedReminderAction.FireImmediately)]
internal sealed class ActivationCronRegistrationTestGrain : Grain, IActivationCronRegistrationTestGrain, AdvancedRemindable
{
    public Task ReceiveReminder(string reminderName, AdvancedTickStatus status) => Task.CompletedTask;
}

internal interface IActivationNoAttributeTestGrain : IGrainWithGuidKey;

internal sealed class ActivationNoAttributeTestGrain : Grain, IActivationNoAttributeTestGrain, AdvancedRemindable
{
    public Task ReceiveReminder(string reminderName, AdvancedTickStatus status) => Task.CompletedTask;
}

internal interface IActivationNonRemindableTestGrain : IGrainWithGuidKey;

[RegisterReminder("non-remindable", dueSeconds: 1, periodSeconds: 5)]
internal sealed class ActivationNonRemindableTestGrain : Grain, IActivationNonRemindableTestGrain;

internal interface IActivationBelowMinimumRegistrationTestGrain : IGrainWithGuidKey;

[RegisterReminder("below-minimum-activation-registration", dueSeconds: 0, periodSeconds: 1)]
internal sealed class ActivationBelowMinimumRegistrationTestGrain : Grain, IActivationBelowMinimumRegistrationTestGrain, AdvancedRemindable
{
    public Task ReceiveReminder(string reminderName, AdvancedTickStatus status) => Task.CompletedTask;
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class RegisterReminderAttributeTests
{
    [Fact]
    public void IntervalCtor_SetsExpectedValues()
    {
        var attribute = new RegisterReminderAttribute(
            "interval-reminder",
            dueSeconds: 15,
            periodSeconds: 60,
            priority: DurableJobPriority.High,
            action: MissedReminderAction.FireImmediately);

        Assert.Equal("interval-reminder", attribute.Name);
        Assert.Equal(TimeSpan.FromSeconds(15), attribute.Due);
        Assert.Equal(TimeSpan.FromSeconds(60), attribute.Period);
        Assert.Null(attribute.Cron);
        Assert.Equal(DurableJobPriority.High, attribute.Priority);
        Assert.Equal(MissedReminderAction.FireImmediately, attribute.Action);
    }

    [Fact]
    public void IntervalCtor_RejectsInvalidInputs()
    {
        Assert.Throws<ArgumentException>(() => new RegisterReminderAttribute("", 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", 1, 1, (DurableJobPriority)sbyte.MaxValue, MissedReminderAction.Skip));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", 1, 1, DurableJobPriority.Normal, (MissedReminderAction)255));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", double.MaxValue, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", 1, double.MaxValue));
    }

    [Fact]
    public void CronCtor_SetsExpectedValues()
    {
        var attribute = new RegisterReminderAttribute(
            "cron-reminder",
            "0 9 * * MON-FRI",
            priority: DurableJobPriority.Normal,
            action: MissedReminderAction.Notify);

        Assert.Equal("cron-reminder", attribute.Name);
        Assert.Equal("0 9 * * MON-FRI", attribute.Cron);
        Assert.Null(attribute.Due);
        Assert.Null(attribute.Period);
        Assert.Equal(DurableJobPriority.Normal, attribute.Priority);
        Assert.Equal(MissedReminderAction.Notify, attribute.Action);
    }

    [Fact]
    public void CronCtor_RejectsInvalidInputs()
    {
        Assert.Throws<ArgumentException>(() => new RegisterReminderAttribute("", "* * * * *"));
        Assert.Throws<ArgumentException>(() => new RegisterReminderAttribute("r", " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", "* * * * *", (DurableJobPriority)sbyte.MaxValue, MissedReminderAction.Skip));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", "* * * * *", DurableJobPriority.Normal, (MissedReminderAction)255));
    }
}

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
                Arg.Any<DurableJobPriority>(),
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
            DurableJobPriority.Normal,
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
                Arg.Any<DurableJobPriority>(),
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
            DurableJobPriority.Normal,
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
                Arg.Any<DurableJobPriority>(),
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
            DurableJobPriority.High,
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
            new AttributeTestJobShardManager(),
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

    private sealed class AttributeTestJobShardManager() : JobShardManager(SiloAddress.Zero)
    {
        public override Task<List<IJobShard>> AssignJobShardsAsync(DateTimeOffset maxDueTime, int maxNewClaims, CancellationToken cancellationToken)
            => Task.FromResult(new List<IJobShard>());

        public override Task<IJobShard> CreateShardAsync(DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string> metadata, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task UnregisterShardAsync(IJobShard shard, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderOptionsValidatorTests
{
    [Fact]
    public void CleanupPolicies_AreDisabledByDefault()
    {
        var options = new AdvancedReminderOptions();

        Assert.False(options.DeleteReminderWhenGrainTypeIsUnavailable);
        Assert.Null(options.MaximumDeliveryAttempts);
    }

    [Fact]
    public void CleanupPolicies_CanBeConfiguredIndependently()
    {
        var unavailableTypeCleanupOnly = new AdvancedReminderOptions
        {
            DeleteReminderWhenGrainTypeIsUnavailable = true,
        };
        var failedDeliveryCleanupOnly = new AdvancedReminderOptions
        {
            MaximumDeliveryAttempts = 3,
        };

        Assert.True(unavailableTypeCleanupOnly.DeleteReminderWhenGrainTypeIsUnavailable);
        Assert.Null(unavailableTypeCleanupOnly.MaximumDeliveryAttempts);
        Assert.False(failedDeliveryCleanupOnly.DeleteReminderWhenGrainTypeIsUnavailable);
        Assert.Equal(3, failedDeliveryCleanupOnly.MaximumDeliveryAttempts);
    }

    [Fact]
    public void ValidateConfiguration_AcceptsValidOptions()
    {
        var options = new AdvancedReminderOptions
        {
            MinimumReminderPeriod = TimeSpan.FromMinutes(1),
            InitializationTimeout = TimeSpan.FromSeconds(30),
            MissedReminderGracePeriod = TimeSpan.FromSeconds(5),
            MaximumDeliveryAttempts = 3,
        };

        var validator = new AdvancedReminderOptionsValidator(NullLogger<AdvancedReminderOptionsValidator>.Instance, Options.Create(options));

        validator.ValidateConfiguration();
    }

    [Fact]
    public void ValidateConfiguration_RejectsNegativeMinimumPeriod()
    {
        var validator = CreateValidator(new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromSeconds(-1) });

        Assert.Throws<OrleansConfigurationException>(() => validator.ValidateConfiguration());
    }

    [Fact]
    public void ValidateConfiguration_RejectsNonPositiveInitializationTimeout()
    {
        var validator = CreateValidator(new AdvancedReminderOptions { InitializationTimeout = TimeSpan.Zero });

        Assert.Throws<OrleansConfigurationException>(() => validator.ValidateConfiguration());
    }

    [Fact]
    public void ValidateConfiguration_RejectsNonPositiveMissedReminderGracePeriod()
    {
        var validator = CreateValidator(new AdvancedReminderOptions { MissedReminderGracePeriod = TimeSpan.Zero });

        Assert.Throws<OrleansConfigurationException>(() => validator.ValidateConfiguration());
    }

    [Theory]
    [InlineData(nameof(AdvancedReminderOptions.InitializationTimeout))]
    public void ValidateConfiguration_RejectsTimerDelayBeyondRuntimeLimit(string optionName)
    {
        var options = new AdvancedReminderOptions();
        var tooLarge = TimeSpan.FromMilliseconds(uint.MaxValue);
        switch (optionName)
        {
            case nameof(AdvancedReminderOptions.InitializationTimeout):
                options.InitializationTimeout = tooLarge;
                break;
        }

        Assert.Throws<OrleansConfigurationException>(() => CreateValidator(options).ValidateConfiguration());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void ValidateConfiguration_RejectsNonPositiveMaximumDeliveryAttempts(int maximumDeliveryAttempts)
    {
        var validator = CreateValidator(new AdvancedReminderOptions { MaximumDeliveryAttempts = maximumDeliveryAttempts });

        Assert.Throws<OrleansConfigurationException>(() => validator.ValidateConfiguration());
    }

    private static AdvancedReminderOptionsValidator CreateValidator(AdvancedReminderOptions options)
        => new(NullLogger<AdvancedReminderOptionsValidator>.Instance, Options.Create(options));
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderRegistrationExtensionsTests
{
    [Fact]
    public async Task RegistryExtension_WithBuilder_DelegatesToScheduleMethod()
    {
        var registry = Substitute.For<IReminderRegistry>();
        var grainId = GrainId.Create("test", "registry-builder");
        var reminder = Substitute.For<IGrainReminder>();
        registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                DurableJobPriority.Normal,
                MissedReminderAction.Skip)
            .Returns(Task.FromResult(reminder));

        var result = await registry.RegisterOrUpdateReminder(grainId, "r", ReminderCronBuilder.WeekdaysAt(9, 30));

        Assert.Same(reminder, result);
        _ = registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Cron
                && schedule.CronExpression == "30 9 * * MON-FRI"
                && schedule.CronTimeZoneId == null),
            DurableJobPriority.Normal,
            MissedReminderAction.Skip);
    }

    [Fact]
    public async Task RegistryExtension_WithExpressionAndPriority_DelegatesToScheduleMethod()
    {
        var registry = Substitute.For<IReminderRegistry>();
        var grainId = GrainId.Create("test", "registry-expression-priority");
        var reminder = Substitute.For<IGrainReminder>();
        var expression = ReminderCronExpression.Parse("*/5 * * * *");
        registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                DurableJobPriority.High,
                MissedReminderAction.Notify)
            .Returns(Task.FromResult(reminder));

        var result = await registry.RegisterOrUpdateReminder(
            grainId,
            "r",
            expression,
            DurableJobPriority.High,
            MissedReminderAction.Notify);

        Assert.Same(reminder, result);
        _ = registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Cron
                && schedule.CronExpression == "*/5 * * * *"
                && schedule.CronTimeZoneId == null),
            DurableJobPriority.High,
            MissedReminderAction.Notify);
    }

    [Fact]
    public async Task ServiceExtension_WithBuilder_DelegatesToScheduleMethod()
    {
        var service = Substitute.For<AdvancedReminderServiceInterface>();
        var grainId = GrainId.Create("test", "service-builder");
        var reminder = Substitute.For<IGrainReminder>();
        service.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                DurableJobPriority.Normal,
                MissedReminderAction.Skip)
            .Returns(Task.FromResult(reminder));

        var result = await service.RegisterOrUpdateReminder(grainId, "r", ReminderCronBuilder.DailyAt(7, 0));

        Assert.Same(reminder, result);
        _ = service.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Cron
                && schedule.CronExpression == "0 7 * * *"
                && schedule.CronTimeZoneId == null),
            DurableJobPriority.Normal,
            MissedReminderAction.Skip);
    }

    [Fact]
    public async Task ServiceExtension_WithExpressionAndPriority_DelegatesToScheduleMethod()
    {
        var service = Substitute.For<AdvancedReminderServiceInterface>();
        var grainId = GrainId.Create("test", "service-expression-priority");
        var reminder = Substitute.For<IGrainReminder>();
        var expression = ReminderCronExpression.Parse("0 */2 * * * *");
        service.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                DurableJobPriority.Normal,
                MissedReminderAction.Skip)
            .Returns(Task.FromResult(reminder));

        var result = await service.RegisterOrUpdateReminder(
            grainId,
            "r",
            expression,
            DurableJobPriority.Normal,
            MissedReminderAction.Skip);

        Assert.Same(reminder, result);
        _ = service.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Cron
                && schedule.CronExpression == "0 */2 * * * *"
                && schedule.CronTimeZoneId == null),
            DurableJobPriority.Normal,
            MissedReminderAction.Skip);
    }

    [Fact]
    public async Task GrainExtension_WithBuilder_DelegatesToRegistry()
    {
        var grainId = GrainId.Create("test", "grain-builder");
        var registry = Substitute.For<IReminderRegistry>();
        var reminder = Substitute.For<IGrainReminder>();
        registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                DurableJobPriority.Normal,
                MissedReminderAction.Skip)
            .Returns(Task.FromResult(reminder));
        var grain = CreateRemindableGrain(grainId, registry);

        var result = await grain.RegisterOrUpdateAdvancedReminder("r", ReminderCronBuilder.DailyAt(10, 15));

        Assert.Same(reminder, result);
        _ = registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Cron
                && schedule.CronExpression == "15 10 * * *"
                && schedule.CronTimeZoneId == null),
            DurableJobPriority.Normal,
            MissedReminderAction.Skip);
    }

    [Fact]
    public async Task GrainExtension_WithPriorityAndAbsoluteDueUtc_DelegatesToRegistry()
    {
        var grainId = GrainId.Create("test", "grain-priority-absolute-due");
        var registry = Substitute.For<IReminderRegistry>();
        var reminder = Substitute.For<IGrainReminder>();
        var dueAtUtc = new DateTime(2026, 2, 1, 11, 0, 0, DateTimeKind.Utc);
        var period = TimeSpan.FromMinutes(1);
        registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                DurableJobPriority.High,
                MissedReminderAction.FireImmediately)
            .Returns(Task.FromResult(reminder));
        var grain = CreateRemindableGrain(grainId, registry);

        var result = await grain.RegisterOrUpdateAdvancedReminder(
            "r",
            dueAtUtc,
            period,
            DurableJobPriority.High,
            MissedReminderAction.FireImmediately);

        Assert.Same(reminder, result);
        _ = registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Interval
                && schedule.DueAtUtc == dueAtUtc
                && schedule.DueTime == null
                && schedule.Period == period
                && schedule.CronExpression == null
                && schedule.CronTimeZoneId == null),
            DurableJobPriority.High,
            MissedReminderAction.FireImmediately);
    }

    [Fact]
    public async Task GrainExtension_WithSchedule_DelegatesToRegistry()
    {
        var grainId = GrainId.Create("test", "grain-schedule");
        var registry = Substitute.For<IReminderRegistry>();
        var reminder = Substitute.For<IGrainReminder>();
        var schedule = ReminderSchedule.Cron("15 10 * * *", "Europe/Paris");
        registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                DurableJobPriority.Normal,
                MissedReminderAction.Skip)
            .Returns(Task.FromResult(reminder));
        var grain = CreateRemindableGrain(grainId, registry);

        var result = await grain.RegisterOrUpdateAdvancedReminder("r", schedule);

        Assert.Same(reminder, result);
        _ = registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(value => ReferenceEquals(value, schedule)),
            DurableJobPriority.Normal,
            MissedReminderAction.Skip);
    }

    [Fact]
    public async Task GrainExtension_WithScheduleAndPriority_DelegatesToRegistry()
    {
        var grainId = GrainId.Create("test", "grain-schedule-priority");
        var registry = Substitute.For<IReminderRegistry>();
        var reminder = Substitute.For<IGrainReminder>();
        var schedule = ReminderSchedule.Interval(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2));
        registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                DurableJobPriority.High,
                MissedReminderAction.Notify)
            .Returns(Task.FromResult(reminder));
        var grain = CreateRemindableGrain(grainId, registry);

        var result = await grain.RegisterOrUpdateAdvancedReminder("r", schedule, DurableJobPriority.High, MissedReminderAction.Notify);

        Assert.Same(reminder, result);
        _ = registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(value => ReferenceEquals(value, schedule)),
            DurableJobPriority.High,
            MissedReminderAction.Notify);
    }

    [Fact]
    public async Task GrainExtension_ThrowsWhenGrainIsNotRemindable()
    {
        var grainId = GrainId.Create("test", "non-remindable");
        var registry = Substitute.For<IReminderRegistry>();
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(grainId);
        context.ActivationServices.Returns(new ServiceCollection().AddSingleton(registry).BuildServiceProvider());

        var grain = Substitute.For<IGrainBase>();
        grain.GrainContext.Returns(context);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await grain.RegisterOrUpdateAdvancedReminder("r", "*/5 * * * *"));
        Assert.Contains(typeof(AdvancedRemindable).FullName!, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegistrationExtensions_ThrowOnNullDependencies()
    {
        var grainId = GrainId.Create("test", "null-dependencies");
        var expression = ReminderCronExpression.Parse("* * * * *");
        var builder = ReminderCronBuilder.EveryMinute();

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await ReminderCronRegistrationExtensions.RegisterOrUpdateReminder((IReminderRegistry)null!, grainId, "r", expression));
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await ReminderCronRegistrationExtensions.RegisterOrUpdateReminder((IReminderRegistry)null!, grainId, "r", builder));
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await ReminderCronRegistrationExtensions.RegisterOrUpdateReminder((AdvancedReminderServiceInterface)null!, grainId, "r", expression));
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await ReminderCronRegistrationExtensions.RegisterOrUpdateReminder((AdvancedReminderServiceInterface)null!, grainId, "r", builder));
    }

    private static IGrainBase CreateRemindableGrain(GrainId grainId, IReminderRegistry registry)
    {
        var services = new ServiceCollection().AddSingleton(registry).BuildServiceProvider();
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(grainId);
        context.ActivationServices.Returns(services);

        var grain = Substitute.For<IGrainBase, AdvancedRemindable>();
        grain.GrainContext.Returns(context);
        return grain;
    }
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderRegistryValidationTests
{
    [Fact]
    public async Task RegisterInterval_RejectsInfiniteDueTime()
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", Timeout.InfiniteTimeSpan, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task RegisterInterval_RejectsNegativeDueTime()
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", TimeSpan.FromSeconds(-1), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task RegisterInterval_RejectsInfinitePeriod()
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", TimeSpan.Zero, Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public async Task RegisterInterval_RejectsNegativePeriod()
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", TimeSpan.Zero, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task RegisterInterval_RejectsPeriodBelowMinimum()
    {
        var registry = CreateRegistry(new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromMinutes(2) });

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", TimeSpan.Zero, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task RegisterOneShot_AllowsZeroPeriodWithoutLoweringMinimumReminderPeriod()
    {
        var service = Substitute.For<AdvancedReminderServiceInterface>();
        var reminder = Substitute.For<IGrainReminder>();
        var grainId = GrainId.Create("test", "one-shot");
        var dueAtUtc = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
        service.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                DurableJobPriority.Normal,
                MissedReminderAction.FireImmediately)
            .Returns(Task.FromResult(reminder));
        var registry = CreateRegistry(
            new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromHours(1) },
            reminderService: service);

        var result = await registry.RegisterOrUpdateReminder(
            grainId,
            "r",
            ReminderSchedule.OneShot(dueAtUtc),
            DurableJobPriority.Normal,
            MissedReminderAction.FireImmediately);

        Assert.Same(reminder, result);
        _ = service.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Interval
                && schedule.IsOneShot
                && schedule.DueAtUtc == dueAtUtc
                && schedule.DueTime == null
                && schedule.Period == TimeSpan.Zero),
            DurableJobPriority.Normal,
            MissedReminderAction.FireImmediately);
    }

    [Fact]
    public void OneShot_WithDateTimeOffset_NormalizesDueTimeToUtc()
    {
        var dueAt = new DateTimeOffset(2030, 4, 15, 19, 30, 0, TimeSpan.FromHours(3));

        var schedule = ReminderSchedule.OneShot(dueAt);

        Assert.Equal(new DateTime(2030, 4, 15, 16, 30, 0, DateTimeKind.Utc), schedule.DueAtUtc);
        Assert.Equal(DateTimeKind.Utc, schedule.DueAtUtc!.Value.Kind);
        Assert.True(schedule.IsOneShot);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void OneShot_WithDateTime_RejectsNonUtcKind(DateTimeKind kind)
    {
        var dueAt = new DateTime(2030, 4, 15, 16, 30, 0, kind);

        var exception = Assert.Throws<ArgumentException>(() => ReminderSchedule.OneShot(dueAt));

        Assert.Equal("dueAtUtc", exception.ParamName);
    }

    [Fact]
    public async Task RegisterInterval_RejectsEmptyName()
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "", TimeSpan.Zero, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task RegisterInterval_RejectsInvalidPriorityOrAction()
    {
        var registry = CreateRegistry();
        var grainId = GrainId.Create("test", "g");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(grainId, "r", TimeSpan.Zero, TimeSpan.FromMinutes(2), (DurableJobPriority)sbyte.MaxValue, MissedReminderAction.Skip));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(grainId, "r", TimeSpan.Zero, TimeSpan.FromMinutes(2), DurableJobPriority.Normal, (MissedReminderAction)255));
    }

    [Fact]
    public async Task RegisterAbsolute_RejectsNonUtcDueTimestamp()
    {
        var registry = CreateRegistry();
        var grainId = GrainId.Create("test", "g");

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await registry.RegisterOrUpdateReminder(grainId, "r", DateTime.Now, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public async Task RegisterCron_RejectsEmptyName()
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), " ", "*/5 * * * *"));
    }

    [Fact]
    public async Task RegisterCron_RejectsEmptyExpression()
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", " "));
    }

    [Fact]
    public async Task RegisterCron_RejectsInvalidExpression()
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAnyAsync<FormatException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", "invalid cron"));
    }

    [Fact]
    public async Task RegisterCron_RejectsInvalidPriorityOrAction()
    {
        var registry = CreateRegistry();
        var grainId = GrainId.Create("test", "g");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(grainId, "r", "*/5 * * * *", (DurableJobPriority)sbyte.MaxValue, MissedReminderAction.Skip));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(grainId, "r", "*/5 * * * *", DurableJobPriority.Normal, (MissedReminderAction)255));
    }

    [Fact]
    public async Task RegisterCron_UsesDurableJobsClockForMinimumPeriodValidation()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 3, 7, 13, 59, 0, TimeSpan.Zero));
        var registry = CreateRegistry(
            new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromHours(23.5) },
            timeProvider: timeProvider);
        var timeZoneId = AdvancedReminderTimeZoneTestHelper.GetUsEasternTimeZone().Id;

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await registry.RegisterOrUpdateReminder(
                GrainId.Create("test", "g"),
                "r",
                ReminderSchedule.Cron("0 9 * * *", timeZoneId),
                DurableJobPriority.Normal,
                MissedReminderAction.Skip));
    }

    [Fact]
    public async Task Register_WithValidInputAndMissingService_ThrowsInvalidOperation()
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", TimeSpan.Zero, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public async Task Register_WithValidInput_DelegatesToReminderService()
    {
        var service = Substitute.For<AdvancedReminderServiceInterface>();
        var reminder = Substitute.For<IGrainReminder>();
        var grainId = GrainId.Create("test", "delegate");
        service.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Is<ReminderSchedule>(schedule =>
                    schedule.Kind == ReminderScheduleKind.Interval
                    && schedule.DueTime == TimeSpan.Zero
                    && schedule.DueAtUtc == null
                    && schedule.Period == TimeSpan.FromMinutes(2)
                    && schedule.CronExpression == null
                    && schedule.CronTimeZoneId == null),
                DurableJobPriority.Normal,
                MissedReminderAction.Skip)
            .Returns(Task.FromResult(reminder));

        var registry = CreateRegistry(reminderService: service);

        var result = await registry.RegisterOrUpdateReminder(grainId, "r", TimeSpan.Zero, TimeSpan.FromMinutes(2));

        Assert.Same(reminder, result);
    }

    private static ReminderRegistry CreateRegistry(
        AdvancedReminderOptions? options = null,
        AdvancedReminderServiceInterface? reminderService = null,
        TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        if (reminderService is not null)
        {
            services.AddSingleton<AdvancedReminderServiceInterface>(reminderService);
        }

        return new ReminderRegistry(
            services.BuildServiceProvider(),
            Options.Create(options ?? new AdvancedReminderOptions()),
            timeProvider ?? TimeProvider.System);
    }
}

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

        Assert.Contains("UseInMemoryDurableJobs()", exception.Message, StringComparison.Ordinal);
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
    public void AddAdvancedReminders_WithLookaheadShorterThanRecoveryCycle_FailsValidation()
    {
        var builder = new TestSiloBuilder();
        builder.Services.AddLogging();
        builder.UseInMemoryAdvancedReminderService();
        builder.Services.Configure<DurableJobsOptions>(options =>
            options.ShardLoadLookaheadPeriod = AdvancedReminderRecoveryGrain.MinimumLookaheadPeriod - TimeSpan.FromSeconds(1));
        using var provider = builder.Services.BuildServiceProvider();
        var validator = provider.GetServices<IConfigurationValidator>()
            .OfType<AdvancedReminderJobBackendValidator>()
            .Single();

        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);

        Assert.Contains(nameof(DurableJobsOptions.ShardLoadLookaheadPeriod), exception.Message, StringComparison.Ordinal);
    }

    private sealed class TestSiloBuilder(IServiceCollection? services = null) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = services ?? new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class AdvancedReminderRecoveryGrainTests
{
    [Fact]
    public async Task InMemoryReminderTableGrain_RangePagingIsBounded()
    {
        var table = new AdvancedReminderTableGrain();
        for (var index = 0; index < 7; index++)
        {
            await table.UpsertRow(new ReminderEntry
            {
                GrainId = GrainId.Create("test", $"paged-{index}"),
                ReminderName = $"reminder-{index}",
                StartAt = DateTime.UtcNow,
                NextDueUtc = DateTime.UtcNow.AddMinutes(1),
                Period = TimeSpan.FromMinutes(1),
            });
        }

        var rows = new List<ReminderEntry>();
        string? continuationToken = null;
        do
        {
            var page = await table.ReadRows(0, 0, maxRows: 2, continuationToken);
            Assert.InRange(page.Reminders.Count, 0, 2);
            rows.AddRange(page.Reminders);
            continuationToken = page.ContinuationToken;
        } while (continuationToken is not null);

        Assert.Equal(7, rows.Count);
        Assert.Equal(7, rows.Select(row => (row.GrainId, row.ReminderName)).Distinct().Count());
    }

    [Fact]
    public async Task InMemoryReminderTableGrain_KeysetPagingDoesNotSkipAfterEarlierDeletion()
    {
        var table = new AdvancedReminderTableGrain();
        for (var index = 0; index < 5; index++)
        {
            await table.UpsertRow(new ReminderEntry
            {
                GrainId = GrainId.Create("test", $"mutation-{index}"),
                ReminderName = $"reminder-{index}",
                StartAt = DateTime.UtcNow,
                NextDueUtc = DateTime.UtcNow.AddMinutes(1),
                Period = TimeSpan.FromMinutes(1),
            });
        }

        var first = await table.ReadRows(0, 0, maxRows: 2, continuationToken: null);
        Assert.Equal(2, first.Reminders.Count);
        Assert.NotNull(first.ContinuationToken);
        var deleted = first.Reminders[0];
        Assert.True(await table.RemoveRow(deleted.GrainId, deleted.ReminderName, deleted.ETag));

        var rows = first.Reminders.ToList();
        var continuationToken = first.ContinuationToken;
        while (continuationToken is not null)
        {
            var page = await table.ReadRows(0, 0, maxRows: 2, continuationToken);
            rows.AddRange(page.Reminders);
            continuationToken = page.ContinuationToken;
        }

        Assert.Equal(5, rows.Select(row => (row.GrainId, row.ReminderName)).Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileAsync_ScansInBoundedRangesAndDispatchesOnlyRequiredRows(bool force)
    {
        const int reminderCount = AdvancedReminderRecoveryGrain.RecoveryPageSize * 2 + 1;
        var entries = Enumerable.Range(0, reminderCount)
            .Select(index => new ReminderEntry
            {
                GrainId = GrainId.Create("test", $"recovery-{index}"),
                ReminderName = $"reminder-{index}",
                StartAt = DateTime.UtcNow.AddMinutes(5),
                NextDueUtc = DateTime.UtcNow.AddMinutes(5),
                Period = TimeSpan.FromMinutes(1),
                ETag = $"etag-{index}",
                ScheduleId = $"schedule-{index}",
                JobId = index == 0 ? string.Empty : $"job-{index}",
                JobShardId = index == 0 ? string.Empty : $"shard-{index}",
            })
            .ToArray();
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        var readCount = 0;
        uint? pagedBegin = null;
        uint? pagedEnd = null;
        reminderTable.ReadRows(Arg.Any<uint>(), Arg.Any<uint>(), AdvancedReminderRecoveryGrain.RecoveryPageSize, Arg.Any<string?>()).Returns(call =>
        {
            Interlocked.Increment(ref readCount);
            var begin = call.ArgAt<uint>(0);
            var end = call.ArgAt<uint>(1);
            var continuationToken = call.ArgAt<string?>(3);
            if (pagedBegin is null)
            {
                pagedBegin = begin;
                pagedEnd = end;
            }

            if (begin != pagedBegin || end != pagedEnd)
            {
                return new ReminderTableData();
            }

            var offset = continuationToken is null
                ? 0
                : int.Parse(continuationToken, CultureInfo.InvariantCulture);
            var rows = entries.Skip(offset).Take(AdvancedReminderRecoveryGrain.RecoveryPageSize).ToArray();
            var nextOffset = offset + rows.Length;
            return new ReminderTableData(
                rows,
                nextOffset < entries.Length ? nextOffset.ToString(CultureInfo.InvariantCulture) : null);
        });
        var dispatcher = Substitute.For<IAdvancedReminderDispatcherGrain>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(Arg.Any<string>(), null).Returns(dispatcher);
        var recovery = new AdvancedReminderRecoveryGrain(
            reminderTable,
            grainFactory,
            NullLogger<AdvancedReminderRecoveryGrain>.Instance);

        await recovery.ReconcileAsync(force, CancellationToken.None);

        await reminderTable.DidNotReceive().StartAsync(Arg.Any<CancellationToken>());
        Assert.Equal(AdvancedReminderRecoveryGrain.ScanBucketsPerReconciliation + 2, readCount);
        await reminderTable.DidNotReceive().ReadRows((uint)0, (uint)0);
        await reminderTable.Received().ReadRows(
            Arg.Any<uint>(),
            Arg.Any<uint>(),
            AdvancedReminderRecoveryGrain.RecoveryPageSize,
            Arg.Any<string?>());
        await dispatcher.Received(1).EnsureScheduledAsync(
            entries[0].GrainId,
            entries[0].ReminderName,
            entries[0].ScheduleId,
            force,
            Arg.Any<CancellationToken>());
        Assert.Equal(
            force ? reminderCount : 1,
            dispatcher.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IAdvancedReminderDispatcherGrain.EnsureScheduledAsync)));
    }

    [Fact]
    public async Task ReconcileAsync_DoesNotReplacePersistedJobHandles()
    {
        var now = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var overdue = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "overdue-recovery"),
            ReminderName = "overdue",
            StartAt = now.UtcDateTime.AddHours(-1),
            NextDueUtc = now.UtcDateTime.AddMinutes(-16),
            Period = TimeSpan.FromMinutes(1),
            ScheduleId = "overdue-schedule",
            JobId = "overdue-job",
            JobShardId = "overdue-shard",
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        var readCount = 0;
        reminderTable.ReadRows(Arg.Any<uint>(), Arg.Any<uint>(), AdvancedReminderRecoveryGrain.RecoveryPageSize, Arg.Any<string?>()).Returns(_ =>
            Interlocked.Increment(ref readCount) == 1
                ? new ReminderTableData([overdue])
                : new ReminderTableData());
        var dispatcher = Substitute.For<IAdvancedReminderDispatcherGrain>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(Arg.Any<string>(), null).Returns(dispatcher);
        var recovery = new AdvancedReminderRecoveryGrain(
            reminderTable,
            grainFactory,
            NullLogger<AdvancedReminderRecoveryGrain>.Instance,
            new RecoveryJobShardManager("overdue-job"),
            timeProvider: timeProvider);

        await recovery.ReconcileAsync(force: false, CancellationToken.None);

        await dispatcher.DidNotReceive().EnsureScheduledAsync(
            overdue.GrainId,
            overdue.ReminderName,
            overdue.ScheduleId,
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_SharedShardAcrossHashBuckets_LoadsMembershipOnce()
    {
        var grainIds = Enumerable.Range(0, 100_000)
            .Select(index => GrainId.Create("test", $"shared-shard-{index}"))
            .GroupBy(grainId => grainId.GetUniformHashCode() >> 20)
            .Take(2)
            .Select(group => group.First())
            .ToArray();
        Assert.Equal(2, grainIds.Length);
        var entries = grainIds.Select((grainId, index) => new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = $"shared-{index}",
            StartAt = DateTime.UtcNow.AddMinutes(-1),
            NextDueUtc = DateTime.UtcNow.AddMinutes(-1),
            Period = TimeSpan.FromMinutes(1),
            ScheduleId = $"schedule-{index}",
            JobId = $"job-{index}",
            JobShardId = "shared-shard",
        }).ToArray();
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRows(
            Arg.Any<uint>(),
            Arg.Any<uint>(),
            AdvancedReminderRecoveryGrain.RecoveryPageSize,
            Arg.Any<string?>()).Returns(call =>
        {
            var range = RangeFactory.CreateRange(call.ArgAt<uint>(0), call.ArgAt<uint>(1));
            return new ReminderTableData(entries.Where(entry => range.InRange(entry.GrainId)));
        });
        var dispatcher = Substitute.For<IAdvancedReminderDispatcherGrain>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(Arg.Any<string>(), null).Returns(dispatcher);
        var shardManager = new RecoveryJobShardManager(entries.Select(entry => entry.JobId).ToArray());
        var recovery = new AdvancedReminderRecoveryGrain(
            reminderTable,
            grainFactory,
            NullLogger<AdvancedReminderRecoveryGrain>.Instance,
            shardManager);

        await recovery.ReconcileAsync(force: false, CancellationToken.None);

        Assert.Equal(1, shardManager.GetJobIdsCallCount);
        Assert.Empty(dispatcher.ReceivedCalls());
    }

    [Fact]
    public async Task ReconcileAsync_ReplacesPersistedHandleWhenJobIsAbsent()
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "missing-job"),
            ReminderName = "missing-job",
            StartAt = DateTime.UtcNow.AddMinutes(-1),
            NextDueUtc = DateTime.UtcNow.AddMinutes(-1),
            Period = TimeSpan.FromMinutes(1),
            ScheduleId = "missing-schedule",
            JobId = "missing-job-id",
            JobShardId = "missing-shard",
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        var readCount = 0;
        reminderTable.ReadRows(Arg.Any<uint>(), Arg.Any<uint>(), AdvancedReminderRecoveryGrain.RecoveryPageSize, Arg.Any<string?>()).Returns(_ =>
            Interlocked.Increment(ref readCount) == 1
                ? new ReminderTableData([entry])
                : new ReminderTableData());
        var dispatcher = Substitute.For<IAdvancedReminderDispatcherGrain>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);
        var recovery = new AdvancedReminderRecoveryGrain(
            reminderTable,
            grainFactory,
            NullLogger<AdvancedReminderRecoveryGrain>.Instance,
            new RecoveryJobShardManager());

        await recovery.ReconcileAsync(force: false, CancellationToken.None);

        await dispatcher.Received(1).EnsureScheduledAsync(
            entry.GrainId,
            entry.ReminderName,
            entry.ScheduleId,
            force: true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_DefersFarFutureEntryUntilLookaheadWindow()
    {
        var now = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "far-future"),
            ReminderName = "far-future",
            StartAt = now.UtcDateTime.AddHours(2),
            NextDueUtc = now.UtcDateTime.AddHours(2),
            Period = TimeSpan.FromMinutes(1),
            ScheduleId = "far-future-schedule",
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        var readCount = 0;
        reminderTable.ReadRows(Arg.Any<uint>(), Arg.Any<uint>(), AdvancedReminderRecoveryGrain.RecoveryPageSize, Arg.Any<string?>()).Returns(_ =>
            Interlocked.Increment(ref readCount) == 1
                ? new ReminderTableData([entry])
                : new ReminderTableData());
        var dispatcher = Substitute.For<IAdvancedReminderDispatcherGrain>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);
        var recovery = new AdvancedReminderRecoveryGrain(
            reminderTable,
            grainFactory,
            NullLogger<AdvancedReminderRecoveryGrain>.Instance,
            new RecoveryJobShardManager(),
            Options.Create(new DurableJobsOptions { ShardLoadLookaheadPeriod = TimeSpan.FromHours(1) }),
            new FakeTimeProvider(now));

        await recovery.ReconcileAsync(force: false, CancellationToken.None);

        Assert.Empty(dispatcher.ReceivedCalls());
    }

    [Fact]
    public async Task ReconcileAsync_WhenOneDispatcherFails_ContinuesScanningOtherReminders()
    {
        var failedEntry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "failed-recovery"),
            ReminderName = "failed",
            StartAt = DateTime.UtcNow.AddMinutes(5),
            Period = TimeSpan.FromMinutes(1),
        };
        var successfulEntry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "successful-recovery"),
            ReminderName = "successful",
            StartAt = DateTime.UtcNow.AddMinutes(5),
            Period = TimeSpan.FromMinutes(1),
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        var readCount = 0;
        reminderTable.ReadRows(Arg.Any<uint>(), Arg.Any<uint>(), AdvancedReminderRecoveryGrain.RecoveryPageSize, Arg.Any<string?>()).Returns(_ =>
            Interlocked.Increment(ref readCount) == 1
                ? new ReminderTableData([failedEntry, successfulEntry])
                : new ReminderTableData());
        var failedDispatcher = Substitute.For<IAdvancedReminderDispatcherGrain>();
        failedDispatcher.EnsureScheduledAsync(
                Arg.Any<GrainId>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Injected reconciliation failure")));
        var successfulDispatcher = Substitute.For<IAdvancedReminderDispatcherGrain>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(failedEntry.GrainId.ToString(), null).Returns(failedDispatcher);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(successfulEntry.GrainId.ToString(), null).Returns(successfulDispatcher);
        var recovery = new AdvancedReminderRecoveryGrain(
            reminderTable,
            grainFactory,
            NullLogger<AdvancedReminderRecoveryGrain>.Instance);

        await recovery.ReconcileAsync(force: false, CancellationToken.None);

        Assert.Equal(256, readCount);
        await successfulDispatcher.Received(1).EnsureScheduledAsync(
            successfulEntry.GrainId,
            successfulEntry.ReminderName,
            successfulEntry.ScheduleId,
            force: false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_WhenOneDispatcherHangs_TimesOutAndContinuesScanning()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero));
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "hanging-recovery"),
            ReminderName = "hanging",
            StartAt = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(5),
            Period = TimeSpan.FromMinutes(1),
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        var readCount = 0;
        reminderTable.ReadRows(Arg.Any<uint>(), Arg.Any<uint>(), AdvancedReminderRecoveryGrain.RecoveryPageSize, Arg.Any<string?>()).Returns(_ =>
            Interlocked.Increment(ref readCount) == 1
                ? new ReminderTableData([entry])
                : new ReminderTableData());
        var dispatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Substitute.For<IAdvancedReminderDispatcherGrain>();
        dispatcher.EnsureScheduledAsync(
                Arg.Any<GrainId>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                dispatchStarted.TrySetResult();
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            });
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);
        var recovery = new AdvancedReminderRecoveryGrain(
            reminderTable,
            grainFactory,
            NullLogger<AdvancedReminderRecoveryGrain>.Instance,
            timeProvider: timeProvider);

        var reconcileTask = recovery.ReconcileAsync(force: false, CancellationToken.None);
        await dispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.Advance(AdvancedReminderRecoveryGrain.ReconciliationEntryTimeout);
        await reconcileTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(256, readCount);
    }

    [Fact]
    public async Task StartAsync_OnlyReconcilesWhenHeartbeatFindsScanDue()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero));
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRows(Arg.Any<uint>(), Arg.Any<uint>(), AdvancedReminderRecoveryGrain.RecoveryPageSize, Arg.Any<string?>()).Returns(new ReminderTableData());
        var recovery = new AdvancedReminderRecoveryGrain(
            reminderTable,
            Substitute.For<IGrainFactory>(),
            NullLogger<AdvancedReminderRecoveryGrain>.Instance,
            timeProvider: timeProvider);

        await recovery.StartAsync(force: false, CancellationToken.None);
        await recovery.StartAsync(force: false, CancellationToken.None);
        Assert.Equal(256, reminderTable.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(Orleans.AdvancedReminders.IReminderTable.ReadRows)));

        timeProvider.Advance(AdvancedReminderRecoveryGrain.ReconciliationPeriod);
        await recovery.StartAsync(force: false, CancellationToken.None);

        Assert.Equal(512, reminderTable.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(Orleans.AdvancedReminders.IReminderTable.ReadRows)));
        var rangeReads = reminderTable.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(Orleans.AdvancedReminders.IReminderTable.ReadRows))
            .ToArray();
        Assert.NotEqual(rangeReads[0].GetArguments()[0], rangeReads[AdvancedReminderRecoveryGrain.ScanBucketsPerReconciliation].GetArguments()[0]);
    }

    private sealed class RecoveryJobShardManager(params string[] jobIds) : JobShardManager(SiloAddress.Zero)
    {
        private readonly HashSet<string> _jobIds = new(jobIds, StringComparer.Ordinal);

        public int GetJobIdsCallCount { get; private set; }

        public override Task<List<IJobShard>> AssignJobShardsAsync(DateTimeOffset maxDueTime, int maxNewClaims, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<IJobShard> CreateShardAsync(
            DateTimeOffset minDueTime,
            DateTimeOffset maxDueTime,
            IDictionary<string, string> metadata,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task UnregisterShardAsync(IJobShard shard, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        internal override ValueTask<HashSet<string>?> GetJobIdsAsync(string shardId, CancellationToken cancellationToken)
        {
            GetJobIdsCallCount++;
            return new(new HashSet<string>(_jobIds, StringComparer.Ordinal));
        }
    }
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class AdvancedReminderServiceTests
{
    [Fact]
    public async Task RegisterOrUpdateReminder_FarFuture_PersistsWithoutCreatingResidentJobShard()
    {
        var now = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "far-future-registration"),
            ReminderName = "far-future",
            StartAt = now.UtcDateTime.AddHours(2),
            NextDueUtc = now.UtcDateTime.AddHours(2),
            Period = TimeSpan.FromMinutes(5),
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-1");
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var service = CreateService(
            reminderTable,
            jobManager: jobManager,
            timeProvider: new FakeTimeProvider(now),
            durableJobsOptions: new DurableJobsOptions
            {
                ShardLoadLookaheadPeriod = TimeSpan.FromHours(1),
            });

        var handle = await service.RegisterOrUpdateCoreAsync(entry, CancellationToken.None);

        Assert.NotNull(handle);
        await reminderTable.Received(1).UpsertRow(Arg.Is<ReminderEntry>(value =>
            value.ReminderName == entry.ReminderName
            && string.IsNullOrEmpty(value.JobId)
            && string.IsNullOrEmpty(value.JobShardId)));
        await jobManager.DidNotReceive().ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Stress")]
    public void ValidateCronSchedule_OneMillionEquivalentRegistrationsReuseBoundedValidationResult()
    {
        ReminderValidation.ClearCronValidationCache();
        var options = new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromMinutes(30) };
        var now = new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc);

        for (var index = 0; index < 1_000_000; index++)
        {
            ReminderValidation.Validate(
                options,
                "daily",
                ReminderSchedule.Cron("0 9 * * *", "Europe/Kyiv"),
                DurableJobPriority.Normal,
                MissedReminderAction.Skip,
                now);
        }

        Assert.Equal(1, ReminderValidation.CronValidationCacheCount);
    }

    [Fact]
    public void ValidateCronSchedule_WhenLaterIntervalIsShorterThanMinimum_Throws()
    {
        var options = new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromDays(31) };
        var now = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);

        var exception = Assert.Throws<ArgumentException>(() => ReminderValidation.Validate(
            options,
            "monthly",
            ReminderSchedule.Cron("0 0 1 * *"),
            DurableJobPriority.Normal,
            MissedReminderAction.Skip,
            now));

        Assert.Contains("30.00:00:00", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateCronSchedule_WhenEverySecondMacroIsShorterThanMinimum_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => ReminderValidation.Validate(
            new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromMinutes(1) },
            "every-second",
            ReminderSchedule.Cron("@every_second"),
            DurableJobPriority.Normal,
            MissedReminderAction.Skip,
            DateTime.UtcNow));

        Assert.Contains("00:00:01", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateCronSchedule_WhenExpressionExceedsStorageLimit_Throws()
    {
        var expression = $"{string.Join(',', Enumerable.Repeat("0", 101))} * * * * *";

        var exception = Assert.Throws<ArgumentException>(() => ReminderValidation.Validate(
            new AdvancedReminderOptions(),
            "long-expression",
            ReminderSchedule.Cron(expression),
            DurableJobPriority.Normal,
            MissedReminderAction.Skip,
            DateTime.UtcNow));

        Assert.Contains("exceeds 200 characters", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateIntervalSchedule_WhenRelativeDueTimeExceedsDateRange_Throws()
    {
        var now = DateTime.SpecifyKind(DateTime.MaxValue.AddMinutes(-1), DateTimeKind.Utc);

        Assert.Throws<ArgumentOutOfRangeException>(() => ReminderValidation.Validate(
            new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromMinutes(1) },
            "outside-date-range",
            ReminderSchedule.Interval(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1)),
            DurableJobPriority.Normal,
            MissedReminderAction.Skip,
            now));
    }

    [Fact]
    public void CalculateNextDue_WhenNextIntervalExceedsDateTimeRange_ReturnsNull()
    {
        var start = DateTime.MaxValue.AddMinutes(-1);
        var entry = new ReminderEntry
        {
            StartAt = start,
            NextDueUtc = start,
            Period = TimeSpan.FromMinutes(2),
        };

        Assert.Null(AdvancedReminderService.CalculateNextDue(entry, start));
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_PublicServiceEnforcesMinimumPeriodAndEnumValidation()
    {
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        var grainFactory = Substitute.For<IGrainFactory>();
        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromMinutes(2) },
            grainFactory: grainFactory);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RegisterOrUpdateReminder(
            GrainId.Create("test", "public-validation"),
            "too-frequent",
            ReminderSchedule.Interval(TimeSpan.Zero, TimeSpan.FromMinutes(1)),
            DurableJobPriority.Normal,
            MissedReminderAction.Skip));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.RegisterOrUpdateReminder(
            GrainId.Create("test", "public-validation"),
            "invalid-enum",
            ReminderSchedule.Interval(TimeSpan.Zero, TimeSpan.FromMinutes(2)),
            (DurableJobPriority)sbyte.MaxValue,
            MissedReminderAction.Skip));

        _ = grainFactory.DidNotReceive().GetGrain<IAdvancedReminderDispatcherGrain>(Arg.Any<string>(), null);
        await reminderTable.DidNotReceive().UpsertRow(Arg.Any<ReminderEntry>());
    }

    [Fact]
    public async Task ReconcileAttributeReminder_WhenUnchangedAtReactivation_PreservesNextTickAndDurableJob()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 16, 11, 0, 0, TimeSpan.Zero));
        var grainId = GrainId.Create("test", "attribute-reactivation");
        var reminderTable = new MutableReminderTable(current: null);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "attribute-reactivation-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);
        var scheduledRequests = new List<ScheduleJobRequest>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequests.Add(request);
                return Task.FromResult(CreateDurableJob(request));
            });
        var service = CreateService(
            reminderTable,
            jobManager: jobManager,
            grainFactory: grainFactory,
            timeProvider: timeProvider);
        dispatcher.Service = service;
        var schedule = ReminderSchedule.Interval(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
        var declarationId = AttributeReminderRegistration.GetDeclarationId(
            schedule,
            DurableJobPriority.Normal,
            MissedReminderAction.Skip);

        await service.ReconcileReminder(
            grainId,
            "every-thirty-minutes",
            schedule,
            DurableJobPriority.Normal,
            MissedReminderAction.Skip,
            declarationId);

        var firstRegistration = await reminderTable.ReadRow(grainId, "every-thirty-minutes");
        Assert.NotNull(firstRegistration);
        Assert.Equal(new DateTime(2026, 8, 16, 11, 30, 0, DateTimeKind.Utc), firstRegistration.NextDueUtc);
        Assert.Single(scheduledRequests);
        Assert.Equal(2, reminderTable.UpsertCount);

        timeProvider.Advance(TimeSpan.FromMinutes(20));
        await service.ReconcileReminder(
            grainId,
            "every-thirty-minutes",
            ReminderSchedule.Interval(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30)),
            DurableJobPriority.Normal,
            MissedReminderAction.Skip,
            declarationId);

        var afterReactivation = await reminderTable.ReadRow(grainId, "every-thirty-minutes");
        Assert.NotNull(afterReactivation);
        Assert.Equal(firstRegistration.NextDueUtc, afterReactivation.NextDueUtc);
        Assert.Equal(firstRegistration.ScheduleId, afterReactivation.ScheduleId);
        Assert.Equal(firstRegistration.JobId, afterReactivation.JobId);
        Assert.Equal(firstRegistration.JobShardId, afterReactivation.JobShardId);
        Assert.Single(scheduledRequests);
        Assert.Equal(2, reminderTable.UpsertCount);
        await jobManager.DidNotReceive().TryCancelDurableJobAsync(Arg.Any<DurableJob>(), Arg.Any<CancellationToken>());

        var remindable = Substitute.For<AdvancedRemindable>();
        remindable.ReceiveReminder(Arg.Any<string>(), Arg.Any<AdvancedTickStatus>()).Returns(Task.CompletedTask);
        grainFactory.GetGrain<AdvancedRemindable>(grainId).Returns(remindable);
        timeProvider.Advance(TimeSpan.FromMinutes(10));
        await service.ProcessDueReminderCoreAsync(
            grainId,
            "every-thirty-minutes",
            afterReactivation.ScheduleId,
            CancellationToken.None);

        var followingOccurrence = await reminderTable.ReadRow(grainId, "every-thirty-minutes");
        Assert.NotNull(followingOccurrence);
        Assert.Equal(new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc), followingOccurrence.NextDueUtc);
        Assert.NotEqual(afterReactivation.ScheduleId, followingOccurrence.ScheduleId);
        Assert.Equal(2, scheduledRequests.Count);
        Assert.Equal(4, reminderTable.UpsertCount);

        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await service.ReconcileReminder(
            grainId,
            "every-thirty-minutes",
            ReminderSchedule.Interval(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30)),
            DurableJobPriority.Normal,
            MissedReminderAction.Skip,
            declarationId);

        var afterFollowingReactivation = await reminderTable.ReadRow(grainId, "every-thirty-minutes");
        Assert.NotNull(afterFollowingReactivation);
        Assert.Equal(followingOccurrence.NextDueUtc, afterFollowingReactivation.NextDueUtc);
        Assert.Equal(followingOccurrence.ScheduleId, afterFollowingReactivation.ScheduleId);
        Assert.Equal(followingOccurrence.JobId, afterFollowingReactivation.JobId);
        Assert.Equal(2, scheduledRequests.Count);
        Assert.Equal(4, reminderTable.UpsertCount);
    }

    [Fact]
    public async Task ReconcileAttributeReminder_WhenDeclarationChanges_ReplacesScheduleAndDurableJob()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 16, 11, 0, 0, TimeSpan.Zero));
        var grainId = GrainId.Create("test", "attribute-update");
        var reminderTable = new MutableReminderTable(current: null);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "attribute-update-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);
        var scheduledRequests = new List<ScheduleJobRequest>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequests.Add(request);
                return Task.FromResult(CreateDurableJob(request));
            });
        jobManager.TryCancelDurableJobAsync(Arg.Any<DurableJob>(), Arg.Any<CancellationToken>()).Returns(true);
        var service = CreateService(
            reminderTable,
            jobManager: jobManager,
            grainFactory: grainFactory,
            timeProvider: timeProvider);
        dispatcher.Service = service;
        var originalSchedule = ReminderSchedule.Interval(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

        await service.ReconcileReminder(
            grainId,
            "changed-attribute",
            originalSchedule,
            DurableJobPriority.Normal,
            MissedReminderAction.Skip,
            AttributeReminderRegistration.GetDeclarationId(
                originalSchedule,
                DurableJobPriority.Normal,
                MissedReminderAction.Skip));
        var original = await reminderTable.ReadRow(grainId, "changed-attribute");
        Assert.NotNull(original);

        timeProvider.Advance(TimeSpan.FromMinutes(20));
        var changedSchedule = ReminderSchedule.Interval(TimeSpan.FromMinutes(40), TimeSpan.FromHours(1));
        await service.ReconcileReminder(
            grainId,
            "changed-attribute",
            changedSchedule,
            DurableJobPriority.High,
            MissedReminderAction.FireImmediately,
            AttributeReminderRegistration.GetDeclarationId(
                changedSchedule,
                DurableJobPriority.High,
                MissedReminderAction.FireImmediately));

        var updated = await reminderTable.ReadRow(grainId, "changed-attribute");
        Assert.NotNull(updated);
        Assert.Equal(new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc), updated.NextDueUtc);
        Assert.Equal(TimeSpan.FromHours(1), updated.Period);
        Assert.Equal(DurableJobPriority.High, updated.Priority);
        Assert.Equal(MissedReminderAction.FireImmediately, updated.Action);
        Assert.NotEqual(original.ScheduleId, updated.ScheduleId);
        Assert.NotEqual(original.JobId, updated.JobId);
        Assert.Equal(2, scheduledRequests.Count);
        Assert.Equal(4, reminderTable.UpsertCount);
        await jobManager.Received(1).TryCancelDurableJobAsync(
            Arg.Is<DurableJob>(job => job.Id == original.JobId && job.ShardId == original.JobShardId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SchedulingFailure_LeavesPendingRowWhichReconciliationRepairsIdempotently()
    {
        var now = DateTime.UtcNow;
        var grainId = GrainId.Create("test", "schedule-failure-recovery");
        var reminderTable = new MutableReminderTable(current: null);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "schedule-failure-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);
        var scheduledRequests = new List<ScheduleJobRequest>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequests.Add(request);
                if (scheduledRequests.Count == 1)
                {
                    throw new InvalidOperationException("injected scheduling failure");
                }

                return Task.FromResult(CreateDurableJob(request));
            });
        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);
        dispatcher.Service = service;
        var entry = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "recoverable",
            StartAt = now.AddMinutes(5),
            NextDueUtc = now.AddMinutes(5),
            Period = TimeSpan.FromMinutes(1),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RegisterOrUpdateCoreAsync(entry, CancellationToken.None));

        var pending = await reminderTable.ReadRow(grainId, entry.ReminderName);
        Assert.NotNull(pending);
        Assert.False(string.IsNullOrWhiteSpace(pending.ScheduleId));
        Assert.Empty(pending.JobId);
        Assert.Empty(pending.JobShardId);

        await service.EnsureScheduledCoreAsync(
            grainId,
            entry.ReminderName,
            pending.ScheduleId,
            force: false,
            CancellationToken.None);

        Assert.Equal(2, scheduledRequests.Count);
        Assert.NotEqual(scheduledRequests[0].Metadata!["schedule-id"], scheduledRequests[1].Metadata!["schedule-id"]);
        var repaired = await reminderTable.ReadRow(grainId, entry.ReminderName);
        Assert.NotNull(repaired);
        Assert.False(string.IsNullOrWhiteSpace(repaired.JobId));
        Assert.False(string.IsNullOrWhiteSpace(repaired.JobShardId));
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenNextJobSchedulingFails_ReconcilesCurrentSchedule()
    {
        var now = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "next-schedule-failure"),
            ReminderName = "recurring",
            StartAt = now.UtcDateTime.AddMinutes(-5),
            NextDueUtc = now.UtcDateTime,
            Period = TimeSpan.FromMinutes(5),
            ETag = "etag-current",
            ScheduleId = "schedule-current",
        };
        var reminderTable = new MutableReminderTable(entry);
        var remindable = new CallbackRemindable(() => Task.CompletedTask);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "next-schedule-failure-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);
        var scheduleAttempts = 0;
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                if (Interlocked.Increment(ref scheduleAttempts) == 1)
                {
                    throw new InvalidOperationException("injected next-job scheduling failure");
                }

                return Task.FromResult(CreateDurableJob(callInfo.Arg<ScheduleJobRequest>()));
            });
        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory, timeProvider: timeProvider);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProcessDueReminderCoreAsync(
            entry.GrainId,
            entry.ReminderName,
            entry.ScheduleId,
            CancellationToken.None));

        var pending = await reminderTable.ReadRow(entry.GrainId, entry.ReminderName);
        Assert.NotNull(pending);
        Assert.NotEqual(entry.ScheduleId, pending.ScheduleId);
        Assert.Empty(pending.JobId);
        Assert.Empty(pending.JobShardId);

        // ProcessDueReminderAsync schedules its repair without an expected id so that this
        // newly-persisted occurrence, rather than the completed one, is repaired.
        await service.EnsureScheduledCoreAsync(
            entry.GrainId,
            entry.ReminderName,
            expectedScheduleId: null,
            force: false,
            CancellationToken.None);

        var repaired = await reminderTable.ReadRow(entry.GrainId, entry.ReminderName);
        Assert.NotNull(repaired);
        Assert.Equal(2, scheduleAttempts);
        Assert.NotEqual(pending.ScheduleId, repaired.ScheduleId);
        Assert.NotEmpty(repaired.JobId);
        Assert.NotEmpty(repaired.JobShardId);
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenCallbackFails_ContinuesRecurringSeries()
    {
        var now = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "callback-failure"),
            ReminderName = "recurring",
            StartAt = now.UtcDateTime.AddMinutes(-5),
            NextDueUtc = now.UtcDateTime,
            Period = TimeSpan.FromMinutes(5),
            ETag = "etag-current",
            ScheduleId = "schedule-current",
        };
        var reminderTable = new MutableReminderTable(entry);
        var remindable = new CallbackRemindable(() => Task.FromException(new InvalidOperationException("callback failed")));
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "callback-failure-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(CreateDurableJob(callInfo.Arg<ScheduleJobRequest>())));
        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory, timeProvider: timeProvider);

        await service.ProcessDueReminderCoreAsync(
            entry.GrainId,
            entry.ReminderName,
            entry.ScheduleId,
            CancellationToken.None);

        Assert.Single(remindable.ReceivedStatuses);
        var current = await reminderTable.ReadRow(entry.GrainId, entry.ReminderName);
        Assert.NotNull(current);
        Assert.Equal(now.UtcDateTime, current.LastFireUtc);
        Assert.Equal(now.UtcDateTime.AddMinutes(5), current.NextDueUtc);
        Assert.NotEmpty(current.JobId);
        Assert.NotEmpty(current.JobShardId);
        await jobManager.Received(1).ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteJobAsync_WhenOverdueRetryCallbackFailsBeforeMaximumDeliveryAttempts_RethrowsForDurableRetry()
    {
        var now = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
        var entry = CreateDueEntry(now, "delivery-retry");
        entry.NextDueUtc = now.UtcDateTime.AddMinutes(-5);
        entry.Action = MissedReminderAction.Skip;
        var reminderTable = new MutableReminderTable(entry);
        var remindable = new CallbackRemindable(() => Task.FromException(new InvalidOperationException("callback failed")));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { MaximumDeliveryAttempts = 3 },
            grainFactory: grainFactory,
            timeProvider: new FakeTimeProvider(now));
        var dispatcher = new AdvancedReminderDispatcherGrain(service);
        var context = CreateReminderJobContext(entry, dequeueCount: 2);

        var exception = await Assert.ThrowsAsync<ReminderDeliveryException>(
            () => dispatcher.ExecuteJobAsync(context, CancellationToken.None));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.NotNull(await reminderTable.ReadRow(entry.GrainId, entry.ReminderName));
        Assert.Empty(reminderTable.RemoveAttempts);
        Assert.Equal(0, reminderTable.UpsertCount);
    }

    [Fact]
    public async Task ExecuteJobAsync_WhenCallbackReachesMaximumDeliveryAttempts_DeletesReminder()
    {
        var now = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
        var entry = CreateDueEntry(now, "delivery-delete");
        var reminderTable = new MutableReminderTable(entry);
        var remindable = new CallbackRemindable(() => Task.FromException(new InvalidOperationException("callback failed")));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { MaximumDeliveryAttempts = 3 },
            grainFactory: grainFactory,
            timeProvider: new FakeTimeProvider(now));
        var dispatcher = new AdvancedReminderDispatcherGrain(service);

        await dispatcher.ExecuteJobAsync(CreateReminderJobContext(entry, dequeueCount: 3), CancellationToken.None);

        Assert.Null(await reminderTable.ReadRow(entry.GrainId, entry.ReminderName));
        Assert.Equal([(entry.ETag, true)], reminderTable.RemoveAttempts);
        Assert.Equal(0, reminderTable.UpsertCount);
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenGrainTypeIsUnavailableAndCleanupEnabled_DeletesReminderWithoutTableScan()
    {
        var now = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
        var entry = CreateDueEntry(now, "unavailable-type");
        var reminderTable = new MutableReminderTable(entry);
        var grainFactory = Substitute.For<IGrainFactory>();
        var clusterState = CreateClusterState();
        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { DeleteReminderWhenGrainTypeIsUnavailable = true },
            grainFactory: grainFactory,
            timeProvider: new FakeTimeProvider(now),
            clusterManifestProvider: clusterState.ManifestProvider,
            clusterMembershipService: clusterState.MembershipService);

        await service.ProcessDueReminderCoreAsync(
            entry.GrainId,
            entry.ReminderName,
            entry.ScheduleId,
            CancellationToken.None,
            durableJobDequeueCount: 1);

        Assert.Null(await reminderTable.ReadRow(entry.GrainId, entry.ReminderName));
        Assert.Equal([(entry.ETag, true)], reminderTable.RemoveAttempts);
        Assert.Empty(grainFactory.ReceivedCalls());
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenGrainTypeIsAvailable_DeliversNormally()
    {
        var now = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
        var entry = CreateDueEntry(now, "available-type");
        var reminderTable = new MutableReminderTable(entry);
        var remindable = new CallbackRemindable(() => Task.CompletedTask);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "available-type-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(CreateDurableJob(callInfo.Arg<ScheduleJobRequest>())));
        var clusterState = CreateClusterState(entry.GrainId.Type);
        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { DeleteReminderWhenGrainTypeIsUnavailable = true },
            jobManager: jobManager,
            grainFactory: grainFactory,
            timeProvider: new FakeTimeProvider(now),
            clusterManifestProvider: clusterState.ManifestProvider,
            clusterMembershipService: clusterState.MembershipService);

        await service.ProcessDueReminderCoreAsync(
            entry.GrainId,
            entry.ReminderName,
            entry.ScheduleId,
            CancellationToken.None,
            durableJobDequeueCount: 1);

        Assert.Single(remindable.ReceivedStatuses);
        Assert.Empty(reminderTable.RemoveAttempts);
        Assert.Equal(2, reminderTable.UpsertCount);
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenActiveSiloManifestIsMissing_DoesNotDeleteReminder()
    {
        var now = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
        var entry = CreateDueEntry(now, "incomplete-manifest");
        var reminderTable = new MutableReminderTable(entry);
        var remindable = new CallbackRemindable(() => Task.CompletedTask);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "incomplete-manifest-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(CreateDurableJob(callInfo.Arg<ScheduleJobRequest>())));
        var clusterState = CreateIncompleteClusterState();
        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { DeleteReminderWhenGrainTypeIsUnavailable = true },
            jobManager: jobManager,
            grainFactory: grainFactory,
            timeProvider: new FakeTimeProvider(now),
            clusterManifestProvider: clusterState.ManifestProvider,
            clusterMembershipService: clusterState.MembershipService);

        await service.ProcessDueReminderCoreAsync(
            entry.GrainId,
            entry.ReminderName,
            entry.ScheduleId,
            CancellationToken.None,
            durableJobDequeueCount: 1);

        Assert.Single(remindable.ReceivedStatuses);
        Assert.Empty(reminderTable.RemoveAttempts);
        Assert.NotNull(await reminderTable.ReadRow(entry.GrainId, entry.ReminderName));
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenJobRunsEarly_ReschedulesWithoutFiring()
    {
        var now = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "early-job"),
            ReminderName = "recurring",
            StartAt = now.UtcDateTime.AddMinutes(5),
            NextDueUtc = now.UtcDateTime.AddMinutes(5),
            Period = TimeSpan.FromMinutes(5),
            ETag = "etag-current",
            ScheduleId = "schedule-current",
        };
        var reminderTable = new MutableReminderTable(entry);
        var remindable = new CallbackRemindable(() => Task.CompletedTask);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "early-job-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(CreateDurableJob(callInfo.Arg<ScheduleJobRequest>())));
        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory, timeProvider: timeProvider);

        await service.ProcessDueReminderCoreAsync(
            entry.GrainId,
            entry.ReminderName,
            entry.ScheduleId,
            CancellationToken.None);

        Assert.Empty(remindable.ReceivedStatuses);
        var current = await reminderTable.ReadRow(entry.GrainId, entry.ReminderName);
        Assert.NotNull(current);
        Assert.Equal(entry.NextDueUtc, current.NextDueUtc);
        Assert.NotEqual(entry.ScheduleId, current.ScheduleId);
        Assert.NotEmpty(current.JobId);
        Assert.NotEmpty(current.JobShardId);
    }

    [Fact]
    public async Task HandlePersistenceFailure_ReconciliationInvalidatesOrphanedDurableJob()
    {
        var now = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var grainId = GrainId.Create("test", "handle-persistence-recovery");
        var reminderTable = new MutableReminderTable(current: null) { FailUpsertCall = 2 };
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "handle-persistence-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);
        var scheduledRequests = new List<ScheduleJobRequest>();
        var scheduledJobs = new List<DurableJob>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequests.Add(request);
                var job = CreateDurableJob(request);
                scheduledJobs.Add(job);
                return Task.FromResult(job);
            });
        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory, timeProvider: timeProvider);
        dispatcher.Service = service;
        var entry = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "recover-handle",
            StartAt = now.UtcDateTime.AddMinutes(-5),
            NextDueUtc = now.UtcDateTime.AddMinutes(-5),
            Period = TimeSpan.FromMinutes(1),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RegisterOrUpdateCoreAsync(entry, CancellationToken.None));
        var pending = await reminderTable.ReadRow(grainId, entry.ReminderName);
        Assert.NotNull(pending);
        Assert.Empty(pending.JobId);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await service.EnsureScheduledCoreAsync(
            grainId,
            entry.ReminderName,
            pending.ScheduleId,
            force: false,
            CancellationToken.None);

        Assert.Equal(2, scheduledRequests.Count);
        Assert.NotEqual(scheduledRequests[0].Metadata!["schedule-id"], scheduledRequests[1].Metadata!["schedule-id"]);
        Assert.Equal(new DateTimeOffset(entry.NextDueUtc!.Value, TimeSpan.Zero), scheduledRequests[0].DueTime);
        Assert.Equal(scheduledRequests[0].DueTime, scheduledRequests[1].DueTime);
        var repaired = await reminderTable.ReadRow(grainId, entry.ReminderName);
        Assert.NotNull(repaired);
        Assert.Equal(scheduledJobs[1].Id, repaired.JobId);
        Assert.Equal(scheduledJobs[1].ShardId, repaired.JobShardId);
        Assert.NotEqual(scheduledJobs[0].Id, repaired.JobId);

        var upsertsBeforeOrphanRuns = reminderTable.UpsertCount;
        await service.ProcessDueReminderCoreAsync(
            grainId,
            entry.ReminderName,
            scheduledRequests[0].Metadata!["schedule-id"],
            CancellationToken.None);

        Assert.Equal(upsertsBeforeOrphanRuns, reminderTable.UpsertCount);
        Assert.Equal(2, scheduledRequests.Count);
    }

    [Fact]
    public void Dispatcher_IsNotPinnedInMemory()
    {
        Assert.Empty(typeof(AdvancedReminderDispatcherGrain).GetCustomAttributes(typeof(KeepAliveAttribute), inherit: true));
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_CancelsPreviousDurableJobAfterReplacementIsPersisted()
    {
        var now = DateTime.UtcNow;
        var grainId = GrainId.Create("test", "cancel-old-job");
        var previous = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "replace",
            StartAt = now.AddHours(1),
            NextDueUtc = now.AddHours(1),
            Period = TimeSpan.FromMinutes(1),
            ETag = "etag-old",
            ScheduleId = "schedule-old",
            JobId = "job-old",
            JobShardId = "shard-old",
        };
        var reminderTable = new MutableReminderTable(previous);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "cancel-old-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(CreateDurableJob(callInfo.Arg<ScheduleJobRequest>())));
        jobManager.TryCancelDurableJobAsync(Arg.Any<DurableJob>(), Arg.Any<CancellationToken>()).Returns(true);
        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);
        dispatcher.Service = service;

        await service.RegisterOrUpdateCoreAsync(new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = previous.ReminderName,
            StartAt = now.AddHours(2),
            NextDueUtc = now.AddHours(2),
            Period = TimeSpan.FromMinutes(2),
        }, CancellationToken.None);

        await jobManager.Received(1).TryCancelDurableJobAsync(
            Arg.Is<DurableJob>(job => job.Id == "job-old" && job.ShardId == "shard-old"),
            CancellationToken.None);
    }

    [Fact]
    public async Task UnregisterReminder_CancelsPersistedDurableJob()
    {
        var now = DateTime.UtcNow;
        var grainId = GrainId.Create("test", "cancel-unregistered-job");
        var current = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "remove",
            StartAt = now.AddHours(1),
            NextDueUtc = now.AddHours(1),
            Period = TimeSpan.FromMinutes(1),
            ETag = "etag-current",
            ScheduleId = "schedule-current",
            JobId = "job-current",
            JobShardId = "shard-current",
        };
        var reminderTable = new MutableReminderTable(current);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "cancel-unregister-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.TryCancelDurableJobAsync(Arg.Any<DurableJob>(), Arg.Any<CancellationToken>()).Returns(true);
        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);

        await service.UnregisterCoreAsync(
            Assert.IsType<Orleans.AdvancedReminders.ReminderData>(current.ToIGrainReminder()),
            CancellationToken.None);

        await jobManager.Received(1).TryCancelDurableJobAsync(
            Arg.Is<DurableJob>(job => job.Id == "job-current" && job.ShardId == "shard-current"),
            CancellationToken.None);
    }

    [Fact]
    public async Task UnregisterReminder_AfterRecurringDelivery_UsesStableRegistrationId()
    {
        var now = DateTime.UtcNow;
        var grainId = GrainId.Create("test", "unregister-after-delivery");
        var registered = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "recurring",
            StartAt = now,
            NextDueUtc = now,
            Period = TimeSpan.FromMinutes(1),
            ETag = "etag-registered",
            ScheduleId = "r1:registration:occurrence-1",
        };
        var reminderTable = new MutableReminderTable(registered);
        var service = CreateService(reminderTable);
        var handle = Assert.IsType<Orleans.AdvancedReminders.ReminderData>(registered.ToIGrainReminder());

        reminderTable.ReplaceCurrent(Clone(
            registered,
            etag: "etag-after-delivery",
            scheduleId: "r1:registration:occurrence-2"));

        await service.UnregisterCoreAsync(handle, CancellationToken.None);

        Assert.Null(await reminderTable.ReadRow(grainId, registered.ReminderName));
        Assert.Equal([("etag-after-delivery", true)], reminderTable.RemoveAttempts);
    }

    [Fact]
    public async Task LifecycleStart_WhenInitializationExceedsConfiguredTimeout_ThrowsTimeoutException()
    {
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.StartAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.Delay(Timeout.InfiniteTimeSpan, callInfo.Arg<CancellationToken>()));
        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { InitializationTimeout = TimeSpan.FromMilliseconds(25) });
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        service.Participate(lifecycle);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => lifecycle.OnStart());

        Assert.Contains("00:00:00.025", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LifecycleStart_WhenProviderIgnoresCancellation_StillEnforcesConfiguredTimeout()
    {
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.StartAsync(Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { InitializationTimeout = TimeSpan.FromMilliseconds(25) });
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        service.Participate(lifecycle);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => lifecycle.OnStart()).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Contains("00:00:00.025", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lifecycle_RecoveryHeartbeatKeepsSingletonRecoverable()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero));
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        var recovery = Substitute.For<IAdvancedReminderRecoveryGrain>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderRecoveryGrain>(0, null).Returns(recovery);
        var service = CreateService(reminderTable, grainFactory: grainFactory, timeProvider: timeProvider);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        service.Participate(lifecycle);

        await lifecycle.OnStart();
        await recovery.Received(1).StartAsync(force: false, Arg.Any<CancellationToken>());

        timeProvider.Advance(AdvancedReminderService.RecoveryHeartbeatPeriod);
        for (var attempt = 0; attempt < 100
            && recovery.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IAdvancedReminderRecoveryGrain.StartAsync)) < 2;
            attempt++)
        {
            await Task.Yield();
        }

        await recovery.Received(2).StartAsync(force: false, Arg.Any<CancellationToken>());
        await lifecycle.OnStop();
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_WithCronSchedule_UpsertsAndSchedulesDurableJob()
    {
        var grainId = GrainId.Create("test", "cron-register");
        var dispatcherGrainId = GrainId.Create("sys", "durable-reminder-dispatcher");
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-1");

        var jobManager = Substitute.For<ILocalDurableJobManager>();
        ScheduleJobRequest? scheduledRequest = null;
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequest = request;
                return Task.FromResult(CreateDurableJob(request));
            });

        var grainFactory = Substitute.For<IGrainFactory>();
        var dispatcher = CreateDispatcherGrain(dispatcherGrainId);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null)
            .Returns(dispatcher);

        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);
        dispatcher.Service = service;

        var reminder = await service.RegisterOrUpdateReminder(
            grainId,
            "cron",
            ReminderSchedule.Cron("0 9 * * *"),
            DurableJobPriority.Normal,
            MissedReminderAction.Skip);

        Assert.Equal("cron", reminder.ReminderName);
        await reminderTable.Received().UpsertRow(Arg.Is<ReminderEntry>(entry =>
            entry.GrainId == grainId
            && entry.ReminderName == "cron"
            && entry.Period == TimeSpan.Zero
            && entry.CronExpression == "0 9 * * *"
            && entry.Priority == DurableJobPriority.Normal
            && entry.Action == MissedReminderAction.Skip
            && entry.NextDueUtc != null));
        await jobManager.Received(1).ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
        var request = Assert.IsType<ScheduleJobRequest>(scheduledRequest);
        Assert.Equal("advanced-reminder:cron", request.JobName);
        Assert.Equal(dispatcherGrainId, request.Target);
        Assert.Equal(grainId.ToString(), request.Metadata!["grain-id"]);
        Assert.Equal("cron", request.Metadata["reminder-name"]);
        Assert.False(string.IsNullOrWhiteSpace(request.Metadata["schedule-id"]));
    }

    [Fact]
    public async Task RegisterOrUpdateReminder_WithAbsoluteIntervalSchedule_UpsertsAndSchedulesDurableJob()
    {
        var grainId = GrainId.Create("test", "absolute-register");
        var dueAtUtc = DateTime.UtcNow.AddMinutes(10);
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-absolute");

        var jobManager = Substitute.For<ILocalDurableJobManager>();
        ScheduleJobRequest? scheduledRequest = null;
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequest = request;
                return Task.FromResult(CreateDurableJob(request));
            });

        var dispatcherGrainId = GrainId.Create("sys", "durable-reminder-dispatcher");
        var dispatcher = CreateDispatcherGrain(dispatcherGrainId);
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null)
            .Returns(dispatcher);

        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);
        dispatcher.Service = service;

        var reminder = await service.RegisterOrUpdateReminder(
            grainId,
            "absolute",
            ReminderSchedule.Interval(dueAtUtc, TimeSpan.FromMinutes(5)),
            DurableJobPriority.High,
            MissedReminderAction.Notify);

        Assert.Equal("absolute", reminder.ReminderName);
        await reminderTable.Received().UpsertRow(Arg.Is<ReminderEntry>(entry =>
            entry.GrainId == grainId
            && entry.ReminderName == "absolute"
            && entry.StartAt == dueAtUtc
            && entry.NextDueUtc == dueAtUtc
            && entry.Period == TimeSpan.FromMinutes(5)
            && entry.Priority == DurableJobPriority.High
            && entry.Action == MissedReminderAction.Notify
            && string.IsNullOrEmpty(entry.CronExpression)));
        var request = Assert.IsType<ScheduleJobRequest>(scheduledRequest);
        Assert.Equal("advanced-reminder:absolute", request.JobName);
        Assert.False(string.IsNullOrWhiteSpace(request.Metadata!["schedule-id"]));
    }

    [Fact]
    public async Task GetReminder_WhenEntryExists_ReturnsMappedHandle()
    {
        var grainId = GrainId.Create("test", "single");
        var entry = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "r",
            ETag = "etag-1",
            CronExpression = "0 9 * * *",
            CronTimeZoneId = "UTC",
            Priority = DurableJobPriority.High,
            Action = MissedReminderAction.Notify,
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(grainId, "r").Returns(Task.FromResult<ReminderEntry?>(entry));
        var service = CreateService(reminderTable);

        var result = await service.GetReminder(grainId, "r");

        var reminder = Assert.IsAssignableFrom<IGrainReminder>(result);
        Assert.Equal("r", reminder.ReminderName);
        Assert.Equal("0 9 * * *", reminder.CronExpression);
        Assert.Equal("UTC", reminder.CronTimeZone);
        Assert.Equal(DurableJobPriority.High, reminder.Priority);
        Assert.Equal(MissedReminderAction.Notify, reminder.Action);
    }

    [Fact]
    public async Task GetReminders_WhenEntriesExist_ReturnsMappedHandles()
    {
        var grainId = GrainId.Create("test", "all");
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRows(grainId).Returns(Task.FromResult(new ReminderTableData(
        [
            new ReminderEntry
            {
                GrainId = grainId,
                ReminderName = "interval",
                ETag = "etag-a",
                Priority = DurableJobPriority.Normal,
                Action = MissedReminderAction.Skip,
            },
            new ReminderEntry
            {
                GrainId = grainId,
                ReminderName = "cron",
                ETag = "etag-b",
                CronExpression = "*/5 * * * *",
                CronTimeZoneId = "UTC",
                Priority = DurableJobPriority.High,
                Action = MissedReminderAction.FireImmediately,
            },
        ])));
        var service = CreateService(reminderTable);

        var result = await service.GetReminders(grainId);

        Assert.Collection(
            result,
            reminder =>
            {
                Assert.Equal("interval", reminder.ReminderName);
                Assert.Null(reminder.CronExpression);
                Assert.Null(reminder.CronTimeZone);
                Assert.Equal(DurableJobPriority.Normal, reminder.Priority);
                Assert.Equal(MissedReminderAction.Skip, reminder.Action);
            },
            reminder =>
            {
                Assert.Equal("cron", reminder.ReminderName);
                Assert.Equal("*/5 * * * *", reminder.CronExpression);
                Assert.Equal("UTC", reminder.CronTimeZone);
                Assert.Equal(DurableJobPriority.High, reminder.Priority);
                Assert.Equal(MissedReminderAction.FireImmediately, reminder.Action);
            });
    }

    [Fact]
    public async Task UnregisterReminder_WithValidHandle_RemovesReminderUsingETag()
    {
        var grainId = GrainId.Create("test", "remove-valid");
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(grainId, "r").Returns(Task.FromResult<ReminderEntry?>(new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "r",
            ETag = "etag-remove",
            ScheduleId = "r1:registration:occurrence",
        }));
        reminderTable.RemoveRow(grainId, "r", "etag-remove").Returns(Task.FromResult(true));
        var service = CreateService(reminderTable);
        var reminder = await service.GetReminder(grainId, "r");

        await service.UnregisterCoreAsync(Assert.IsType<Orleans.AdvancedReminders.ReminderData>(reminder), CancellationToken.None);

        await reminderTable.Received(1).RemoveRow(grainId, "r", "etag-remove");
    }

    [Fact]
    public async Task UnregisterReminder_WithForeignHandle_Throws()
    {
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        var service = CreateService(reminderTable);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            async () => await service.UnregisterReminder(Substitute.For<IGrainReminder>()));

        Assert.Equal("reminder", exception.ParamName);
        await reminderTable.DidNotReceive().RemoveRow(Arg.Any<GrainId>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task UnregisterReminder_WithStaleHandle_RejectsDeleteAndPreservesLatestReminder()
    {
        var now = DateTime.UtcNow;
        var grainId = GrainId.Create("test", "stale-remove");
        var original = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "r",
            StartAt = now,
            NextDueUtc = now,
            Period = TimeSpan.FromMinutes(1),
            ETag = "etag-v1",
            ScheduleId = "r1:registration-v1:occurrence",
        };
        var reminderTable = new MutableReminderTable(original);
        var service = CreateService(reminderTable);
        var staleHandle = original.ToIGrainReminder();

        reminderTable.ReplaceCurrent(Clone(
            original,
            etag: "etag-v2",
            scheduleId: "r1:registration-v2:occurrence"));

        await Assert.ThrowsAsync<AdvancedReminderException>(
            () => service.UnregisterCoreAsync(
                Assert.IsType<Orleans.AdvancedReminders.ReminderData>(staleHandle),
                CancellationToken.None));

        var current = await reminderTable.ReadRow(grainId, "r");
        Assert.NotNull(current);
        Assert.Equal("etag-v2", current.ETag);
        Assert.Empty(reminderTable.RemoveAttempts);
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenReminderIsMissing_Returns()
    {
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(Arg.Any<GrainId>(), Arg.Any<string>()).Returns(Task.FromResult<ReminderEntry?>(null));

        var service = CreateService(reminderTable);

        await service.ProcessDueReminderCoreAsync(GrainId.Create("test", "missing"), "r", expectedScheduleId: null, CancellationToken.None);

        await reminderTable.Received(1).ReadRow(Arg.Any<GrainId>(), "r");
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenETagDoesNotMatch_ReturnsWithoutUpsert()
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "etag"),
            ReminderName = "r",
            StartAt = DateTime.UtcNow.AddMinutes(-5),
            NextDueUtc = DateTime.UtcNow.AddMinutes(-5),
            Period = TimeSpan.FromMinutes(1),
            ETag = "current",
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        var service = CreateService(reminderTable);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: "stale", CancellationToken.None);

        await reminderTable.DidNotReceive().UpsertRow(Arg.Any<ReminderEntry>());
        await reminderTable.DidNotReceive().RemoveRow(Arg.Any<GrainId>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenMissedSkipAndNoFutureSchedule_RemovesReminder()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "remove"),
            ReminderName = "r",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-10),
            Period = TimeSpan.Zero,
            Action = MissedReminderAction.Skip,
            ETag = "etag",
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        reminderTable.RemoveRow(entry.GrainId, entry.ReminderName, entry.ETag).Returns(true);

        var service = CreateService(reminderTable, options: new AdvancedReminderOptions { MissedReminderGracePeriod = TimeSpan.FromSeconds(1) });

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        await reminderTable.Received(1).RemoveRow(entry.GrainId, entry.ReminderName, entry.ETag);
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenMissedNotifyAndNoFutureSchedule_RemovesReminderWithoutCallingGrain()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "notify-remove"),
            ReminderName = "notify",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-10),
            Period = TimeSpan.Zero,
            Action = MissedReminderAction.Notify,
            ETag = "etag-notify",
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        reminderTable.RemoveRow(entry.GrainId, entry.ReminderName, entry.ETag).Returns(true);

        var remindable = Substitute.For<AdvancedRemindable>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);

        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { MissedReminderGracePeriod = TimeSpan.FromSeconds(1) },
            grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        await reminderTable.Received(1).RemoveRow(entry.GrainId, entry.ReminderName, entry.ETag);
        Assert.Empty(remindable.ReceivedCalls());
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenMissedSkipAndFutureSchedule_DoesNotCallGrainAndReschedules()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "skip-reschedule"),
            ReminderName = "skip",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-4),
            Period = TimeSpan.FromMinutes(5),
            Action = MissedReminderAction.Skip,
            ETag = "etag-skip",
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-skip-2");

        var remindable = Substitute.For<AdvancedRemindable>();
        var dispatcherGrainId = GrainId.Create("sys", "durable-reminder-dispatcher");
        var dispatcher = CreateDispatcherGrain(dispatcherGrainId);
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);

        var jobManager = Substitute.For<ILocalDurableJobManager>();
        ScheduleJobRequest? scheduledRequest = null;
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequest = request;
                return Task.FromResult(CreateDurableJob(request));
            });

        var service = CreateService(reminderTable, options: new AdvancedReminderOptions { MissedReminderGracePeriod = TimeSpan.FromSeconds(1) }, jobManager: jobManager, grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        Assert.Empty(remindable.ReceivedCalls());
        await reminderTable.Received(2).UpsertRow(Arg.Is<ReminderEntry>(updated =>
            updated.LastFireUtc == null
            && updated.NextDueUtc > now
            && updated.Period == entry.Period));
        await jobManager.Received(1).ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
        Assert.Equal("advanced-reminder:skip", Assert.NotNull(scheduledRequest).JobName);
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenMissedNotifyAndFutureSchedule_DoesNotCallGrainAndReschedules()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "notify-reschedule"),
            ReminderName = "notify",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-4),
            Period = TimeSpan.FromMinutes(5),
            Action = MissedReminderAction.Notify,
            ETag = "etag-notify-future",
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-notify-future-2");

        var remindable = Substitute.For<AdvancedRemindable>();
        var dispatcherGrainId = GrainId.Create("sys", "durable-reminder-dispatcher");
        var dispatcher = CreateDispatcherGrain(dispatcherGrainId);
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);

        var jobManager = Substitute.For<ILocalDurableJobManager>();
        ScheduleJobRequest? scheduledRequest = null;
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequest = request;
                return Task.FromResult(CreateDurableJob(request));
            });

        var service = CreateService(reminderTable, options: new AdvancedReminderOptions { MissedReminderGracePeriod = TimeSpan.FromSeconds(1) }, jobManager: jobManager, grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        Assert.Empty(remindable.ReceivedCalls());
        await reminderTable.Received(2).UpsertRow(Arg.Is<ReminderEntry>(updated =>
            updated.LastFireUtc == null
            && updated.NextDueUtc > now
            && updated.Period == entry.Period));
        await jobManager.Received(1).ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
        Assert.Equal("advanced-reminder:notify", Assert.NotNull(scheduledRequest).JobName);
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenMissedFireImmediatelyAndNoFutureSchedule_FiresThenRemovesReminder()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "fire-remove"),
            ReminderName = "fire",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-10),
            Period = TimeSpan.Zero,
            Action = MissedReminderAction.FireImmediately,
            ETag = "etag-fire-remove",
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        reminderTable.RemoveRow(entry.GrainId, entry.ReminderName, entry.ETag).Returns(true);

        var remindable = Substitute.For<AdvancedRemindable>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        var service = CreateService(reminderTable, options: new AdvancedReminderOptions { MissedReminderGracePeriod = TimeSpan.FromSeconds(1) }, grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        AssertReminderReceived(remindable, "fire", status =>
        {
            Assert.Equal(TimeSpan.Zero, status.Period);
            Assert.True(status.CurrentTickTime >= now);
        });
        await reminderTable.Received(1).RemoveRow(entry.GrainId, entry.ReminderName, entry.ETag);
        await reminderTable.DidNotReceive().UpsertRow(Arg.Any<ReminderEntry>());
    }

    [Fact]
    public async Task OneShotReminder_RegistersFiresOnceAndRemovesRegistration()
    {
        var now = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var grainId = GrainId.Create("test", "one-shot-lifecycle");
        var reminderTable = new MutableReminderTable(current: null);
        var remindable = new CallbackRemindable(() => Task.CompletedTask);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "one-shot-lifecycle-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(grainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(CreateDurableJob(callInfo.Arg<ScheduleJobRequest>())));
        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromHours(1) },
            jobManager: jobManager,
            grainFactory: grainFactory,
            timeProvider: timeProvider);
        dispatcher.Service = service;
        var dueAt = now.AddMinutes(5);

        await service.RegisterOrUpdateReminder(
            grainId,
            "one-shot",
            ReminderSchedule.OneShot(dueAt.UtcDateTime),
            DurableJobPriority.Normal,
            MissedReminderAction.FireImmediately);
        var registered = await reminderTable.ReadRow(grainId, "one-shot");

        Assert.NotNull(registered);
        Assert.Equal(TimeSpan.Zero, registered.Period);
        Assert.Equal(dueAt.UtcDateTime, registered.NextDueUtc);
        Assert.NotEmpty(registered.ScheduleId);
        Assert.NotEmpty(registered.JobId);
        timeProvider.Advance(TimeSpan.FromMinutes(5));

        await service.ProcessDueReminderCoreAsync(
            grainId,
            "one-shot",
            registered.ScheduleId,
            CancellationToken.None);

        Assert.Single(remindable.ReceivedStatuses);
        Assert.Equal(TimeSpan.Zero, remindable.ReceivedStatuses[0].Period);
        Assert.Null(await reminderTable.ReadRow(grainId, "one-shot"));
        Assert.Contains(reminderTable.RemoveAttempts, attempt => attempt.Removed);
        await jobManager.Received(1).ScheduleJobAsync(
            Arg.Any<ScheduleJobRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenMissedFireImmediatelyAndFutureSchedule_FiresAndReschedules()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "fire-reschedule"),
            ReminderName = "fire-future",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-4),
            Period = TimeSpan.FromMinutes(5),
            Action = MissedReminderAction.FireImmediately,
            ETag = "etag-fire-future",
        };

        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-fire-future-2");

        var remindable = Substitute.For<AdvancedRemindable>();
        var dispatcherGrainId = GrainId.Create("sys", "durable-reminder-dispatcher");
        var dispatcher = CreateDispatcherGrain(dispatcherGrainId);
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);

        var jobManager = Substitute.For<ILocalDurableJobManager>();
        ScheduleJobRequest? scheduledRequest = null;
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequest = request;
                return Task.FromResult(CreateDurableJob(request));
            });

        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { MissedReminderGracePeriod = TimeSpan.FromSeconds(1) },
            jobManager: jobManager,
            grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        AssertReminderReceived(remindable, "fire-future", status =>
        {
            Assert.Equal(entry.StartAt, status.FirstTickTime);
            Assert.Equal(entry.Period, status.Period);
            Assert.True(status.CurrentTickTime >= now);
        });
        await reminderTable.Received(2).UpsertRow(Arg.Is<ReminderEntry>(updated =>
            updated.LastFireUtc != null
            && updated.NextDueUtc > now
            && updated.Period == entry.Period));
        await reminderTable.DidNotReceive().RemoveRow(entry.GrainId, entry.ReminderName, entry.ETag);
        await jobManager.Received(1).ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
        Assert.Equal("advanced-reminder:fire-future", Assert.NotNull(scheduledRequest).JobName);
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenReminderIsRemovedDuringCallback_DoesNotResurrectReminder()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "remove-during-fire"),
            ReminderName = "interval",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-1),
            Period = TimeSpan.FromMinutes(5),
            Action = MissedReminderAction.FireImmediately,
            ETag = "etag-remove-during-fire",
        };
        var reminderTable = new MutableReminderTable(entry);
        var remindable = new CallbackRemindable(() =>
        {
            reminderTable.DeleteCurrent();
            return Task.CompletedTask;
        });
        var dispatcherGrainId = GrainId.Create("sys", "durable-reminder-dispatcher");
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        var dispatcher = CreateDispatcherGrain(dispatcherGrainId);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null)
            .Returns(dispatcher);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        Assert.Single(remindable.ReceivedStatuses);
        Assert.Null(await reminderTable.ReadRow(entry.GrainId, entry.ReminderName));
        Assert.Equal(0, reminderTable.UpsertCount);
        await jobManager.DidNotReceive().ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenReminderIsUpdatedDuringCallback_DoesNotOverwriteNewSchedule()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "update-during-fire"),
            ReminderName = "cron",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-1),
            Period = TimeSpan.Zero,
            CronExpression = "*/5 * * * *",
            CronTimeZoneId = "UTC",
            Action = MissedReminderAction.FireImmediately,
            ETag = "etag-before-update",
        };
        var updated = new ReminderEntry
        {
            GrainId = entry.GrainId,
            ReminderName = entry.ReminderName,
            StartAt = now.AddHours(1),
            NextDueUtc = now.AddHours(1),
            Period = TimeSpan.Zero,
            CronExpression = "15 8 * * *",
            CronTimeZoneId = ReminderCronSchedule.NormalizeTimeZoneIdForStorage(AdvancedReminderTimeZoneTestHelper.GetUsEasternTimeZone()) ?? "America/New_York",
            Priority = DurableJobPriority.High,
            Action = MissedReminderAction.Notify,
            ETag = "etag-after-update",
        };
        var reminderTable = new MutableReminderTable(entry);
        var remindable = new CallbackRemindable(() =>
        {
            reminderTable.ReplaceCurrent(updated);
            return Task.CompletedTask;
        });
        var dispatcherGrainId = GrainId.Create("sys", "durable-reminder-dispatcher");
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        var dispatcher = CreateDispatcherGrain(dispatcherGrainId);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null)
            .Returns(dispatcher);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        var current = await reminderTable.ReadRow(entry.GrainId, entry.ReminderName);
        Assert.NotNull(current);
        Assert.Equal(updated.ETag, current.ETag);
        Assert.Equal(updated.CronExpression, current.CronExpression);
        Assert.Equal(updated.CronTimeZoneId, current.CronTimeZoneId);
        Assert.Equal(updated.NextDueUtc, current.NextDueUtc);
        Assert.Equal(updated.Action, current.Action);
        Assert.Null(current.LastFireUtc);
        Assert.Equal(0, reminderTable.UpsertCount);
        await jobManager.DidNotReceive().ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDueReminderAsync_WhenReminderTimeZoneChangesBeforeOldJobFires_OldJobNoOps()
    {
        var current = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "tz-change"),
            ReminderName = "cron",
            StartAt = DateTime.UtcNow.AddHours(2),
            NextDueUtc = DateTime.UtcNow.AddHours(2),
            Period = TimeSpan.Zero,
            CronExpression = "0 9 * * *",
            CronTimeZoneId = ReminderCronSchedule.NormalizeTimeZoneIdForStorage(AdvancedReminderTimeZoneTestHelper.GetIndiaTimeZone()) ?? "Asia/Kolkata",
            Action = MissedReminderAction.FireImmediately,
            ETag = "etag-new-timezone",
        };
        var reminderTable = new MutableReminderTable(current);
        var remindable = new CallbackRemindable(() => Task.CompletedTask);
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(current.GrainId).Returns(remindable);
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(current.GrainId, current.ReminderName, expectedScheduleId: "etag-old-timezone", CancellationToken.None);

        Assert.Empty(remindable.ReceivedStatuses);
        Assert.Equal(0, reminderTable.UpsertCount);
        Assert.Equal(current.ETag, (await reminderTable.ReadRow(current.GrainId, current.ReminderName))!.ETag);
        await jobManager.DidNotReceive().ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDueReminderAsync_UsesOriginalGrainIdWhenResolvingRemindable()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("custom-remindable-type", "grain-key"),
            ReminderName = "typed",
            StartAt = now.AddMinutes(-5),
            NextDueUtc = now.AddSeconds(-5),
            Period = TimeSpan.FromMinutes(1),
            Action = MissedReminderAction.FireImmediately,
            ETag = "etag-typed",
        };

        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-typed-2");

        var remindable = Substitute.For<AdvancedRemindable>();
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "durable-reminder-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null).Returns(dispatcher);

        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(CreateDurableJob(callInfo.Arg<ScheduleJobRequest>())));

        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        grainFactory.Received(1).GetGrain<AdvancedRemindable>(entry.GrainId);
        AssertReminderReceived(remindable, "typed", status => Assert.Equal(entry.Period, status.Period));
    }

    [Fact]
    public async Task ProcessDueReminderAsync_ForIntervalReminder_FiresAndReschedules()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "interval-due"),
            ReminderName = "interval",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-1),
            Period = TimeSpan.FromMinutes(2),
            Action = MissedReminderAction.FireImmediately,
            ETag = "etag-interval",
        };

        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-interval-2");

        var remindable = Substitute.For<AdvancedRemindable>();
        var dispatcherGrainId = GrainId.Create("sys", "durable-reminder-dispatcher");
        var grainFactory = Substitute.For<IGrainFactory>();
        var dispatcher = CreateDispatcherGrain(dispatcherGrainId);
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null)
            .Returns(dispatcher);

        var jobManager = Substitute.For<ILocalDurableJobManager>();
        ScheduleJobRequest? scheduledRequest = null;
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequest = request;
                return Task.FromResult(CreateDurableJob(request));
            });

        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        AssertReminderReceived(remindable, "interval", status =>
        {
            Assert.Equal(entry.StartAt, status.FirstTickTime);
            Assert.Equal(entry.Period, status.Period);
            Assert.True(status.CurrentTickTime >= now);
        });
        await reminderTable.Received(2).UpsertRow(Arg.Is<ReminderEntry>(updated =>
            updated.GrainId == entry.GrainId
            && updated.ReminderName == entry.ReminderName
            && updated.LastFireUtc != null
            && updated.NextDueUtc != null
            && updated.NextDueUtc > now
            && updated.Period == entry.Period
            && updated.CronExpression == entry.CronExpression));
        await jobManager.Received(1).ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
        var request = Assert.IsType<ScheduleJobRequest>(scheduledRequest);
        Assert.Equal("advanced-reminder:interval", request.JobName);
        Assert.Equal(dispatcherGrainId, request.Target);
    }

    [Fact]
    public async Task ProcessDueReminderAsync_ForCronReminder_FiresAndReschedulesWithZeroPeriod()
    {
        var now = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "cron-due"),
            ReminderName = "cron",
            StartAt = now.AddMinutes(-10),
            NextDueUtc = now.AddMinutes(-1),
            Period = TimeSpan.Zero,
            CronExpression = "*/5 * * * *",
            Action = MissedReminderAction.FireImmediately,
            ETag = "etag-cron",
        };

        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult<ReminderEntry?>(entry));
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-cron-2");

        var remindable = Substitute.For<AdvancedRemindable>();
        var dispatcherGrainId = GrainId.Create("sys", "durable-reminder-dispatcher");
        var grainFactory = Substitute.For<IGrainFactory>();
        var dispatcher = CreateDispatcherGrain(dispatcherGrainId);
        grainFactory.GetGrain<AdvancedRemindable>(entry.GrainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString(), null)
            .Returns(dispatcher);

        var jobManager = Substitute.For<ILocalDurableJobManager>();
        ScheduleJobRequest? scheduledRequest = null;
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequest = request;
                return Task.FromResult(CreateDurableJob(request));
            });

        var service = CreateService(reminderTable, jobManager: jobManager, grainFactory: grainFactory);

        await service.ProcessDueReminderCoreAsync(entry.GrainId, entry.ReminderName, expectedScheduleId: entry.ETag, CancellationToken.None);

        AssertReminderReceived(remindable, "cron", status =>
        {
            Assert.Equal(entry.StartAt, status.FirstTickTime);
            Assert.Equal(TimeSpan.Zero, status.Period);
            Assert.True(status.CurrentTickTime >= now);
        });
        await reminderTable.Received(2).UpsertRow(Arg.Is<ReminderEntry>(updated =>
            updated.GrainId == entry.GrainId
            && updated.ReminderName == entry.ReminderName
            && updated.LastFireUtc != null
            && updated.NextDueUtc != null
            && updated.NextDueUtc > now
            && updated.Period == TimeSpan.Zero
            && updated.CronExpression == entry.CronExpression));
        await jobManager.Received(1).ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
        var request = Assert.IsType<ScheduleJobRequest>(scheduledRequest);
        Assert.Equal("advanced-reminder:cron", request.JobName);
        Assert.Equal(dispatcherGrainId, request.Target);
    }

    [Fact]
    public async Task CronReminder_WithoutTimeZone_FiresInUtcAfterTimeProviderAdvancesAndReschedules()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 8, 59, 50, TimeSpan.Zero));
        var expectedFirstDueUtc = new DateTime(2026, 1, 15, 9, 0, 0, DateTimeKind.Utc);
        var expectedNextDueUtc = new DateTime(2026, 1, 16, 9, 0, 0, DateTimeKind.Utc);
        var grainId = GrainId.Create("test", "cron-utc-runtime");
        var reminderTable = new MutableReminderTable(current: null);
        var scheduledRequests = new List<ScheduleJobRequest>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequests.Add(request);
                return Task.FromResult(CreateDurableJob(request));
            });

        var remindable = new CallbackRemindable(() => Task.CompletedTask);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "cron-utc-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(grainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);
        var service = CreateService(
            reminderTable,
            jobManager: jobManager,
            grainFactory: grainFactory,
            timeProvider: timeProvider);
        dispatcher.Service = service;

        await service.RegisterOrUpdateReminder(
            grainId,
            "utc-daily",
            ReminderSchedule.Cron("0 9 * * *"),
            DurableJobPriority.Normal,
            MissedReminderAction.Skip);

        var initialEntry = await reminderTable.ReadRow(grainId, "utc-daily");
        Assert.NotNull(initialEntry);
        Assert.Equal(expectedFirstDueUtc, initialEntry.NextDueUtc);
        Assert.Equal(string.Empty, initialEntry.CronTimeZoneId);
        Assert.Equal(new DateTimeOffset(expectedFirstDueUtc), Assert.Single(scheduledRequests).DueTime);

        await ExecuteScheduledReminderAfterAdvancingTimeAsync(service, timeProvider, scheduledRequests[0], remindable);

        var status = Assert.Single(remindable.ReceivedStatuses);
        Assert.Equal(expectedFirstDueUtc, status.FirstTickTime);
        Assert.Equal(expectedFirstDueUtc, status.CurrentTickTime);
        Assert.Equal(TimeSpan.Zero, status.Period);

        var updatedEntry = await reminderTable.ReadRow(grainId, "utc-daily");
        Assert.NotNull(updatedEntry);
        Assert.Equal(expectedFirstDueUtc, updatedEntry.LastFireUtc);
        Assert.Equal(expectedNextDueUtc, updatedEntry.NextDueUtc);
        Assert.Equal(string.Empty, updatedEntry.CronTimeZoneId);
        Assert.Equal(new DateTimeOffset(expectedNextDueUtc), Assert.Single(scheduledRequests.Skip(1)).DueTime);
    }

    [Fact]
    public async Task CronReminder_WithTimeZone_FiresAtLocalTimeAfterTimeProviderAdvancesAndReschedules()
    {
        var timeZone = AdvancedReminderTimeZoneTestHelper.GetParisTimeZone();
        var timeZoneId = ReminderCronSchedule.NormalizeTimeZoneIdForStorage(timeZone) ?? timeZone.Id;
        var expectedFirstDueUtc = AdvancedReminderTimeZoneTestHelper.ToUtc(timeZone, 2026, 1, 15, 9, 0, 0);
        var expectedNextDueUtc = AdvancedReminderTimeZoneTestHelper.ToUtc(timeZone, 2026, 1, 16, 9, 0, 0);
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(expectedFirstDueUtc.AddSeconds(-10)));
        var grainId = GrainId.Create("test", "cron-paris-runtime");
        var reminderTable = new MutableReminderTable(current: null);
        var scheduledRequests = new List<ScheduleJobRequest>();
        var jobManager = Substitute.For<ILocalDurableJobManager>();
        jobManager.ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduleJobRequest>();
                scheduledRequests.Add(request);
                return Task.FromResult(CreateDurableJob(request));
            });

        var remindable = new CallbackRemindable(() => Task.CompletedTask);
        var dispatcher = CreateDispatcherGrain(GrainId.Create("sys", "cron-paris-dispatcher"));
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<AdvancedRemindable>(grainId).Returns(remindable);
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);
        var service = CreateService(
            reminderTable,
            jobManager: jobManager,
            grainFactory: grainFactory,
            timeProvider: timeProvider);
        dispatcher.Service = service;

        await service.RegisterOrUpdateReminder(
            grainId,
            "paris-daily",
            ReminderSchedule.Cron("0 9 * * *", timeZone.Id),
            DurableJobPriority.Normal,
            MissedReminderAction.Skip);

        var initialEntry = await reminderTable.ReadRow(grainId, "paris-daily");
        Assert.NotNull(initialEntry);
        Assert.Equal(new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc), expectedFirstDueUtc);
        Assert.Equal(expectedFirstDueUtc, initialEntry.NextDueUtc);
        Assert.Equal(timeZoneId, initialEntry.CronTimeZoneId);
        Assert.Equal(new DateTimeOffset(expectedFirstDueUtc), Assert.Single(scheduledRequests).DueTime);

        await ExecuteScheduledReminderAfterAdvancingTimeAsync(service, timeProvider, scheduledRequests[0], remindable);

        var status = Assert.Single(remindable.ReceivedStatuses);
        Assert.Equal(expectedFirstDueUtc, status.FirstTickTime);
        Assert.Equal(expectedFirstDueUtc, status.CurrentTickTime);
        Assert.Equal(TimeSpan.Zero, status.Period);

        var updatedEntry = await reminderTable.ReadRow(grainId, "paris-daily");
        Assert.NotNull(updatedEntry);
        Assert.Equal(expectedFirstDueUtc, updatedEntry.LastFireUtc);
        Assert.Equal(expectedNextDueUtc, updatedEntry.NextDueUtc);
        Assert.Equal(timeZoneId, updatedEntry.CronTimeZoneId);
        Assert.Equal(new DateTimeOffset(expectedNextDueUtc), Assert.Single(scheduledRequests.Skip(1)).DueTime);
    }

    [Fact]
    public void ReminderTableData_ToString_ClosesOuterCollection()
    {
        Assert.Equal("[0 reminders: []].", new ReminderTableData().ToString());
    }

    [Fact]
    public void TryGetReminderMetadata_ReturnsExpectedValues()
    {
        var grainId = GrainId.Create("test", "metadata");
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grain-id"] = grainId.ToString(),
            ["reminder-name"] = "r",
            ["etag"] = "etag-1",
        };

        var result = AdvancedReminderService.TryGetReminderMetadata(metadata, out var parsedGrainId, out var reminderName, out var eTag);

        Assert.True(result);
        Assert.Equal(grainId, parsedGrainId);
        Assert.Equal("r", reminderName);
        Assert.Equal("etag-1", eTag);
    }

    [Fact]
    public void TryGetReminderMetadata_ReturnsFalseWhenRequiredFieldsAreMissing()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grain-id"] = GrainId.Create("test", "metadata").ToString(),
        };

        var result = AdvancedReminderService.TryGetReminderMetadata(metadata, out _, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryGetReminderMetadata_ReturnsFalseForNullOrWhitespaceName()
    {
        var grainId = GrainId.Create("test", "metadata");
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grain-id"] = grainId.ToString(),
            ["reminder-name"] = " ",
        };

        Assert.False(AdvancedReminderService.TryGetReminderMetadata(null, out _, out _, out _));
        Assert.False(AdvancedReminderService.TryGetReminderMetadata(metadata, out _, out _, out _));
    }

    [Fact]
    public void TryGetReminderMetadata_ReturnsFalseForMalformedGrainId()
    {
        var metadata = new Dictionary<string, string>
        {
            ["grain-id"] = "not a valid grain id",
            ["reminder-name"] = "r",
            ["schedule-id"] = "schedule",
        };

        Assert.False(AdvancedReminderService.TryGetReminderMetadata(metadata, out _, out _, out _));
    }

    private static AdvancedReminderService CreateService(
        Orleans.AdvancedReminders.IReminderTable reminderTable,
        AdvancedReminderOptions? options = null,
        ILocalDurableJobManager? jobManager = null,
        IGrainFactory? grainFactory = null,
        JobShardManager? jobShardManager = null,
        TimeProvider? timeProvider = null,
        IClusterManifestProvider? clusterManifestProvider = null,
        IClusterMembershipService? clusterMembershipService = null,
        DurableJobsOptions? durableJobsOptions = null)
    {
        jobManager ??= Substitute.For<ILocalDurableJobManager>();
        grainFactory ??= Substitute.For<IGrainFactory>();
        jobShardManager ??= new TestJobShardManager();
        return new AdvancedReminderService(
            reminderTable,
            jobManager,
            jobShardManager,
            grainFactory,
            Options.Create(options ?? new AdvancedReminderOptions()),
            NullLogger<AdvancedReminderService>.Instance,
            timeProvider ?? TimeProvider.System,
            clusterManifestProvider ?? Substitute.For<IClusterManifestProvider>(),
            clusterMembershipService ?? Substitute.For<IClusterMembershipService>(),
            Options.Create(durableJobsOptions ?? new DurableJobsOptions
            {
                ShardLoadLookaheadPeriod = TimeSpan.FromDays(3_650),
            }));
    }

    private static ReminderEntry CreateDueEntry(DateTimeOffset now, string key)
        => new()
        {
            GrainId = GrainId.Create("test", key),
            ReminderName = "recurring",
            StartAt = now.UtcDateTime.AddMinutes(-5),
            NextDueUtc = now.UtcDateTime,
            Period = TimeSpan.FromMinutes(5),
            ETag = $"etag-{key}",
            ScheduleId = $"schedule-{key}",
        };

    private static IJobRunContext CreateReminderJobContext(ReminderEntry entry, int dequeueCount)
    {
        var context = Substitute.For<IJobRunContext>();
        context.DequeueCount.Returns(dequeueCount);
        context.Job.Returns(new DurableJob
        {
            Id = $"job-{entry.GrainId.Key}",
            Name = $"advanced-reminder:{entry.ReminderName}",
            DueTime = new DateTimeOffset(entry.NextDueUtc ?? entry.StartAt, TimeSpan.Zero),
            TargetGrainId = GrainId.Create("advanced-reminder-dispatcher", entry.GrainId.ToString()),
            ShardId = "test-shard",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grain-id"] = entry.GrainId.ToString(),
                ["reminder-name"] = entry.ReminderName,
                ["schedule-id"] = entry.ScheduleId,
            },
        });
        return context;
    }

    private static (IClusterManifestProvider ManifestProvider, IClusterMembershipService MembershipService) CreateClusterState(
        params GrainType[] grainTypes)
    {
        var grainProperties = new GrainProperties(ImmutableDictionary.Create<string, string>(StringComparer.Ordinal));
        var grains = grainTypes.ToImmutableDictionary(static grainType => grainType, _ => grainProperties);
        var manifest = new GrainManifest(
            grains,
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        var siloAddress = SiloAddress.New(System.Net.IPAddress.Loopback, 11111, 1);
        var clusterManifest = new ClusterManifest(
            new MajorMinorVersion(1, 0),
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty.Add(siloAddress, manifest));
        var membership = new ClusterMembershipSnapshot(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(
                siloAddress,
                new ClusterMember(siloAddress, SiloStatus.Active, "silo-1")),
            new MembershipVersion(1));
        return CreateClusterState(clusterManifest, membership);
    }

    private static (IClusterManifestProvider ManifestProvider, IClusterMembershipService MembershipService) CreateIncompleteClusterState()
    {
        var localSilo = SiloAddress.New(System.Net.IPAddress.Loopback, 11111, 1);
        var remoteSilo = SiloAddress.New(System.Net.IPAddress.Loopback, 11112, 1);
        var emptyManifest = new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty,
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        var clusterManifest = new ClusterManifest(
            new MajorMinorVersion(1, 0),
            ImmutableDictionary<SiloAddress, GrainManifest>.Empty.Add(localSilo, emptyManifest));
        var membership = new ClusterMembershipSnapshot(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty
                .Add(localSilo, new ClusterMember(localSilo, SiloStatus.Active, "silo-1"))
                .Add(remoteSilo, new ClusterMember(remoteSilo, SiloStatus.Active, "silo-2")),
            new MembershipVersion(1));
        return CreateClusterState(clusterManifest, membership);
    }

    private static (IClusterManifestProvider ManifestProvider, IClusterMembershipService MembershipService) CreateClusterState(
        ClusterManifest clusterManifest,
        ClusterMembershipSnapshot membership)
    {
        var manifestProvider = Substitute.For<IClusterManifestProvider>();
        manifestProvider.Current.Returns(clusterManifest);
        var membershipService = Substitute.For<IClusterMembershipService>();
        membershipService.CurrentSnapshot.Returns(membership);
        return (manifestProvider, membershipService);
    }

    private static DurableJob CreateDurableJob(ScheduleJobRequest request)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = request.JobName,
            DueTime = request.DueTime,
            TargetGrainId = request.Target,
            ShardId = "test-shard",
            Metadata = request.Metadata,
        };

    private static TestAdvancedReminderDispatcherGrain CreateDispatcherGrain(GrainId grainId)
        => new TestAdvancedReminderDispatcherGrain(grainId);

    private static async Task ExecuteScheduledReminderAfterAdvancingTimeAsync(
        AdvancedReminderService service,
        FakeTimeProvider timeProvider,
        ScheduleJobRequest request,
        CallbackRemindable remindable)
    {
        var queue = new InMemoryJobQueue(timeProvider);
        queue.Enqueue(CreateDurableJob(request), dequeueCount: 0);
        queue.MarkAsComplete();
        await using var enumerator = queue.GetAsyncEnumerator();
        var dequeueTask = enumerator.MoveNextAsync().AsTask();

        Assert.False(dequeueTask.IsCompleted);
        Assert.Empty(remindable.ReceivedStatuses);

        var advanceBy = request.DueTime - timeProvider.GetUtcNow();
        Assert.True(advanceBy > TimeSpan.Zero);
        timeProvider.Advance(advanceBy);

        Assert.True(await dequeueTask.WaitAsync(TimeSpan.FromSeconds(5)));
        var dispatcher = new AdvancedReminderDispatcherGrain(service);
        await dispatcher.ExecuteJobAsync(enumerator.Current, CancellationToken.None);
    }

    private static ReminderEntry Clone(
        ReminderEntry entry,
        string? etag = null,
        string? cronExpression = null,
        string? cronTimeZoneId = null,
        DateTime? nextDueUtc = null,
        DateTime? lastFireUtc = null,
        string? scheduleId = null)
        => new()
        {
            GrainId = entry.GrainId,
            ReminderName = entry.ReminderName,
            StartAt = entry.StartAt,
            Period = entry.Period,
            ETag = etag ?? entry.ETag,
            CronExpression = cronExpression ?? entry.CronExpression,
            CronTimeZoneId = cronTimeZoneId ?? entry.CronTimeZoneId,
            NextDueUtc = nextDueUtc ?? entry.NextDueUtc,
            LastFireUtc = lastFireUtc ?? entry.LastFireUtc,
            Priority = entry.Priority,
            Action = entry.Action,
            ScheduleId = scheduleId ?? entry.ScheduleId,
            JobId = entry.JobId,
            JobShardId = entry.JobShardId,
        };

    private static void AssertReminderReceived(AdvancedRemindable remindable, string reminderName, Action<AdvancedTickStatus> assertStatus)
    {
        var receiveCalls = remindable.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(AdvancedRemindable.ReceiveReminder))
            .ToArray();

        var call = Assert.Single(receiveCalls);
        var arguments = call.GetArguments();
        Assert.Equal(reminderName, Assert.IsType<string>(arguments[0]));
        assertStatus(Assert.IsType<AdvancedTickStatus>(arguments[1]));
    }

    private sealed class MutableReminderTable : Orleans.AdvancedReminders.IReminderTable
    {
        private ReminderEntry? _current;

        public MutableReminderTable(ReminderEntry? current) => _current = current is null ? null : Clone(current);

        public int UpsertCount { get; private set; }

        public int FailUpsertCall { get; init; }

        public List<(string ETag, bool Removed)> RemoveAttempts { get; } = new();

        public void ReplaceCurrent(ReminderEntry? entry) => _current = entry is null ? null : Clone(entry);

        public void DeleteCurrent() => _current = null;

        public Task<ReminderTableData> ReadRows(GrainId grainId)
            => Task.FromResult(_current is not null && _current.GrainId == grainId
                ? new ReminderTableData([Clone(_current)])
                : new ReminderTableData());

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
            => Task.FromResult(_current is null ? new ReminderTableData() : new ReminderTableData([Clone(_current)]));

        public Task<ReminderTableData> ReadRows(uint begin, uint end, int maxRows, string? continuationToken)
            => Task.FromResult(
                continuationToken is null && _current is not null
                    ? new ReminderTableData([Clone(_current)])
                    : new ReminderTableData());

        public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
            => Task.FromResult(_current is not null && _current.GrainId == grainId && _current.ReminderName == reminderName
                ? Clone(_current)
                : null);

        public Task<string> UpsertRow(ReminderEntry entry)
        {
            UpsertCount++;
            if (UpsertCount == FailUpsertCall)
            {
                throw new InvalidOperationException("injected reminder table upsert failure");
            }

            var updated = Clone(entry, etag: $"{entry.ETag}-next");
            _current = updated;
            return Task.FromResult(updated.ETag);
        }

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            var removed = _current is not null
                && _current.GrainId == grainId
                && _current.ReminderName == reminderName
                && string.Equals(_current.ETag, eTag, StringComparison.Ordinal);
            RemoveAttempts.Add((eTag, removed));
            if (removed)
            {
                _current = null;
            }

            return Task.FromResult(removed);
        }

        public Task TestOnlyClearTable()
        {
            _current = null;
            return Task.CompletedTask;
        }
    }

    private sealed class TestJobShardManager() : JobShardManager(SiloAddress.Zero)
    {
        public override Task<List<IJobShard>> AssignJobShardsAsync(DateTimeOffset maxDueTime, int maxNewClaims, CancellationToken cancellationToken)
            => Task.FromResult(new List<IJobShard>());

        public override Task<IJobShard> CreateShardAsync(DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string> metadata, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task UnregisterShardAsync(IJobShard shard, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class CallbackRemindable(Func<Task> onReminder) : AdvancedRemindable
    {
        public List<AdvancedTickStatus> ReceivedStatuses { get; } = new();

        public async Task ReceiveReminder(string reminderName, AdvancedTickStatus status)
        {
            ReceivedStatuses.Add(status);
            await onReminder();
        }
    }

    private sealed class TestAdvancedReminderDispatcherGrain : IAdvancedReminderDispatcherGrain, IGrainBase
    {
        public TestAdvancedReminderDispatcherGrain(GrainId grainId)
        {
            var context = Substitute.For<IGrainContext>();
            context.GrainId.Returns(grainId);
            GrainContext = context;
        }

        public IGrainContext GrainContext { get; }

        public AdvancedReminderService? Service { get; set; }

        public Task<IGrainReminder> RegisterOrUpdateAsync(ReminderEntry entry)
            => Service!.RegisterOrUpdateCoreAsync(entry, CancellationToken.None);

        public Task<IGrainReminder> ReconcileAttributeAsync(ReminderEntry entry, string declarationId)
            => Service!.ReconcileAttributeCoreAsync(entry, declarationId, CancellationToken.None);

        public Task<string> UpsertAndScheduleAsync(ReminderEntry entry, CancellationToken cancellationToken)
            => Service!.UpsertAndScheduleCoreAsync(entry, cancellationToken);

        public Task UnregisterAsync(Orleans.AdvancedReminders.ReminderData reminder)
            => Service!.UnregisterCoreAsync(reminder, CancellationToken.None);

        public Task ProcessDueReminderAsync(GrainId grainId, string reminderName, string? expectedScheduleId, CancellationToken cancellationToken)
            => Service!.ProcessDueReminderCoreAsync(grainId, reminderName, expectedScheduleId, cancellationToken);

        public Task EnsureScheduledAsync(GrainId grainId, string reminderName, string? expectedScheduleId, bool force, CancellationToken cancellationToken)
            => Service!.EnsureScheduledCoreAsync(grainId, reminderName, expectedScheduleId, force, cancellationToken);

        public Task ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
