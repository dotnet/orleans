#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans;
using Orleans.Configuration.Internal;
using Orleans.DurableJobs;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Orleans.AdvancedReminders.Timers;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Runtime;
using Xunit;
using AdvancedReminderOptions = Orleans.AdvancedReminders.ReminderOptions;
using AdvancedReminderOptionsValidator = Orleans.AdvancedReminders.ReminderOptionsValidator;
using AdvancedReminderServiceInterface = Orleans.AdvancedReminders.IReminderService;
using AdvancedRemindable = Orleans.AdvancedReminders.IRemindable;
using AdvancedTickStatus = Orleans.AdvancedReminders.Runtime.TickStatus;
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
    priority: ReminderPriority.High,
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
            priority: ReminderPriority.High,
            action: MissedReminderAction.FireImmediately);

        Assert.Equal("interval-reminder", attribute.Name);
        Assert.Equal(TimeSpan.FromSeconds(15), attribute.Due);
        Assert.Equal(TimeSpan.FromSeconds(60), attribute.Period);
        Assert.Null(attribute.Cron);
        Assert.Equal(ReminderPriority.High, attribute.Priority);
        Assert.Equal(MissedReminderAction.FireImmediately, attribute.Action);
    }

    [Fact]
    public void IntervalCtor_RejectsInvalidInputs()
    {
        Assert.Throws<ArgumentException>(() => new RegisterReminderAttribute("", 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", 1, 1, (ReminderPriority)255, MissedReminderAction.Skip));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", 1, 1, ReminderPriority.Normal, (MissedReminderAction)255));
    }

    [Fact]
    public void CronCtor_SetsExpectedValues()
    {
        var attribute = new RegisterReminderAttribute(
            "cron-reminder",
            "0 9 * * MON-FRI",
            priority: ReminderPriority.Normal,
            action: MissedReminderAction.Notify);

        Assert.Equal("cron-reminder", attribute.Name);
        Assert.Equal("0 9 * * MON-FRI", attribute.Cron);
        Assert.Null(attribute.Due);
        Assert.Null(attribute.Period);
        Assert.Equal(ReminderPriority.Normal, attribute.Priority);
        Assert.Equal(MissedReminderAction.Notify, attribute.Action);
    }

    [Fact]
    public void CronCtor_RejectsInvalidInputs()
    {
        Assert.Throws<ArgumentException>(() => new RegisterReminderAttribute("", "* * * * *"));
        Assert.Throws<ArgumentException>(() => new RegisterReminderAttribute("r", " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", "* * * * *", (ReminderPriority)255, MissedReminderAction.Skip));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegisterReminderAttribute("r", "* * * * *", ReminderPriority.Normal, (MissedReminderAction)255));
    }
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

        var reminderService = Substitute.For<AdvancedReminderServiceInterface>();
        var existingReminder = Substitute.For<IGrainReminder>();
        reminderService.GetReminder(Arg.Any<GrainId>(), "interval-activation-registration")
            .Returns(Task.FromResult<IGrainReminder?>(existingReminder));

        var (grainId, observer) = ConfigureAndCaptureObserver(configurator, reminderService);

        await observer.OnStart(CancellationToken.None);

        _ = reminderService.DidNotReceive().RegisterOrUpdateReminder(
            grainId,
            "interval-activation-registration",
            Arg.Any<ReminderSchedule>(),
            Arg.Any<ReminderPriority>(),
            Arg.Any<MissedReminderAction>());
    }

    [Fact]
    public async Task OnStart_RegistersMissingIntervalReminder()
    {
        var provider = CreateProvider(typeof(ActivationIntervalRegistrationTestGrain));
        Assert.True(provider.TryGetConfigurator(GrainType.Create("test"), EmptyGrainProperties, out var configurator));

        var reminderService = Substitute.For<AdvancedReminderServiceInterface>();
        reminderService.GetReminder(Arg.Any<GrainId>(), "interval-activation-registration")
            .Returns(Task.FromResult<IGrainReminder?>(null));
        reminderService.RegisterOrUpdateReminder(
                Arg.Any<GrainId>(),
                Arg.Any<string>(),
                Arg.Any<ReminderSchedule>(),
                Arg.Any<ReminderPriority>(),
                Arg.Any<MissedReminderAction>())
            .Returns(Task.FromResult(Substitute.For<IGrainReminder>()));

        var (grainId, observer) = ConfigureAndCaptureObserver(configurator, reminderService);

        await observer.OnStart(CancellationToken.None);

        _ = reminderService.Received(1).RegisterOrUpdateReminder(
            grainId,
            "interval-activation-registration",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Interval
                && schedule.DueTime == TimeSpan.FromSeconds(5)
                && schedule.DueAtUtc == null
                && schedule.Period == TimeSpan.FromSeconds(30)
                && schedule.CronExpression == null
                && schedule.CronTimeZoneId == null),
            ReminderPriority.Normal,
            MissedReminderAction.Skip);
    }

    [Fact]
    public async Task OnStart_RegistersMissingCronReminder()
    {
        var provider = CreateProvider(typeof(ActivationCronRegistrationTestGrain));
        Assert.True(provider.TryGetConfigurator(GrainType.Create("test"), EmptyGrainProperties, out var configurator));

        var reminderService = Substitute.For<AdvancedReminderServiceInterface>();
        reminderService.GetReminder(Arg.Any<GrainId>(), "cron-activation-registration")
            .Returns(Task.FromResult<IGrainReminder?>(null));
        reminderService.RegisterOrUpdateReminder(
                Arg.Any<GrainId>(),
                Arg.Any<string>(),
                Arg.Any<ReminderSchedule>(),
                Arg.Any<ReminderPriority>(),
                Arg.Any<MissedReminderAction>())
            .Returns(Task.FromResult(Substitute.For<IGrainReminder>()));

        var (grainId, observer) = ConfigureAndCaptureObserver(configurator, reminderService);

        await observer.OnStart(CancellationToken.None);

        _ = reminderService.Received(1).RegisterOrUpdateReminder(
            grainId,
            "cron-activation-registration",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Cron
                && schedule.CronExpression == "0 9 * * MON-FRI"
                && schedule.CronTimeZoneId == null
                && schedule.DueTime == null
                && schedule.DueAtUtc == null
                && schedule.Period == null),
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
        AdvancedReminderServiceInterface? reminderService)
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

[TestCategory("Reminders")]
public class ReminderOptionsValidatorTests
{
    [Fact]
    public void ValidateConfiguration_AcceptsValidOptions()
    {
        var options = new AdvancedReminderOptions
        {
            MinimumReminderPeriod = TimeSpan.FromMinutes(1),
            InitializationTimeout = TimeSpan.FromSeconds(30),
            LookAheadWindow = TimeSpan.FromMinutes(3),
            PollInterval = TimeSpan.FromSeconds(5),
            BaseBucketSize = 1,
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
    public void ValidateConfiguration_RejectsNonPositiveLookAheadWindow()
    {
        var validator = CreateValidator(new AdvancedReminderOptions { LookAheadWindow = TimeSpan.Zero });

        Assert.Throws<OrleansConfigurationException>(() => validator.ValidateConfiguration());
    }

    [Fact]
    public void ValidateConfiguration_RejectsNonPositivePollInterval()
    {
        var validator = CreateValidator(new AdvancedReminderOptions { PollInterval = TimeSpan.Zero });

        Assert.Throws<OrleansConfigurationException>(() => validator.ValidateConfiguration());
    }

    [Fact]
    public void ValidateConfiguration_RejectsZeroBaseBucketSize()
    {
        var validator = CreateValidator(new AdvancedReminderOptions { BaseBucketSize = 0 });

        Assert.Throws<OrleansConfigurationException>(() => validator.ValidateConfiguration());
    }

    private static AdvancedReminderOptionsValidator CreateValidator(AdvancedReminderOptions options)
        => new(NullLogger<AdvancedReminderOptionsValidator>.Instance, Options.Create(options));
}

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
                ReminderPriority.Normal,
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
            ReminderPriority.Normal,
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
                ReminderPriority.High,
                MissedReminderAction.Notify)
            .Returns(Task.FromResult(reminder));

        var result = await registry.RegisterOrUpdateReminder(
            grainId,
            "r",
            expression,
            ReminderPriority.High,
            MissedReminderAction.Notify);

        Assert.Same(reminder, result);
        _ = registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Cron
                && schedule.CronExpression == "*/5 * * * *"
                && schedule.CronTimeZoneId == null),
            ReminderPriority.High,
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
                ReminderPriority.Normal,
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
            ReminderPriority.Normal,
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
                ReminderPriority.Normal,
                MissedReminderAction.Skip)
            .Returns(Task.FromResult(reminder));

        var result = await service.RegisterOrUpdateReminder(
            grainId,
            "r",
            expression,
            ReminderPriority.Normal,
            MissedReminderAction.Skip);

        Assert.Same(reminder, result);
        _ = service.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Cron
                && schedule.CronExpression == "0 */2 * * * *"
                && schedule.CronTimeZoneId == null),
            ReminderPriority.Normal,
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
                ReminderPriority.Normal,
                MissedReminderAction.Skip)
            .Returns(Task.FromResult(reminder));
        var grain = CreateRemindableGrain(grainId, registry);

        var result = await grain.RegisterOrUpdateReminder("r", ReminderCronBuilder.DailyAt(10, 15));

        Assert.Same(reminder, result);
        _ = registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Cron
                && schedule.CronExpression == "15 10 * * *"
                && schedule.CronTimeZoneId == null),
            ReminderPriority.Normal,
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
                ReminderPriority.High,
                MissedReminderAction.FireImmediately)
            .Returns(Task.FromResult(reminder));
        var grain = CreateRemindableGrain(grainId, registry);

        var result = await grain.RegisterOrUpdateReminder(
            "r",
            dueAtUtc,
            period,
            ReminderPriority.High,
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
            ReminderPriority.High,
            MissedReminderAction.FireImmediately);
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
            async () => await grain.RegisterOrUpdateReminder("r", "*/5 * * * *"));
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
            async () => await registry.RegisterOrUpdateReminder(grainId, "r", TimeSpan.Zero, TimeSpan.FromMinutes(2), (ReminderPriority)255, MissedReminderAction.Skip));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(grainId, "r", TimeSpan.Zero, TimeSpan.FromMinutes(2), ReminderPriority.Normal, (MissedReminderAction)255));
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
            async () => await registry.RegisterOrUpdateReminder(grainId, "r", "*/5 * * * *", (ReminderPriority)255, MissedReminderAction.Skip));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(grainId, "r", "*/5 * * * *", ReminderPriority.Normal, (MissedReminderAction)255));
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
                ReminderPriority.Normal,
                MissedReminderAction.Skip)
            .Returns(Task.FromResult(reminder));

        var registry = CreateRegistry(reminderService: service);

        var result = await registry.RegisterOrUpdateReminder(grainId, "r", TimeSpan.Zero, TimeSpan.FromMinutes(2));

        Assert.Same(reminder, result);
    }

    private static ReminderRegistry CreateRegistry(AdvancedReminderOptions? options = null, AdvancedReminderServiceInterface? reminderService = null)
    {
        var services = new ServiceCollection();
        if (reminderService is not null)
        {
            services.AddSingleton<AdvancedReminderServiceInterface>(reminderService);
        }

        return new ReminderRegistry(services.BuildServiceProvider(), Options.Create(options ?? new AdvancedReminderOptions()));
    }
}

[TestCategory("Reminders")]
public class SiloBuilderReminderExtensionsTests
{
    [Fact]
    public void AddAdvancedReminders_RegistersAdvancedReminderService()
    {
        var services = new ServiceCollection();

        services.AddAdvancedReminders();

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(AdvancedReminderService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(AdvancedReminderServiceInterface));
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
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IReminderRegistry));
    }

    [Fact]
    public void AddAdvancedReminders_BuilderOverload_RegistersAdvancedReminderService()
    {
        var builder = new TestSiloBuilder();

        builder.AddAdvancedReminders();

        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(AdvancedReminderService));
        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(AdvancedReminderServiceInterface));
    }

    [Fact]
    public void AddAdvancedReminders_ConfigureOptions_UpdatesReminderOptions()
    {
        var services = new ServiceCollection();

        services.AddAdvancedReminders(options =>
        {
            options.LookAheadWindow = TimeSpan.FromSeconds(9);
            options.MinimumReminderPeriod = TimeSpan.FromSeconds(3);
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AdvancedReminderOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(9), options.LookAheadWindow);
        Assert.Equal(TimeSpan.FromSeconds(3), options.MinimumReminderPeriod);
    }

    [Fact]
    public void AddAdvancedReminders_BuilderOverloadWithConfigureOptions_UpdatesReminderOptions()
    {
        var builder = new TestSiloBuilder();

        builder.AddAdvancedReminders(options => options.LookAheadWindow = TimeSpan.FromSeconds(11));

        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AdvancedReminderOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(11), options.LookAheadWindow);
    }

    [Fact]
    public void UseInMemoryAdvancedReminderService_RegistersInMemoryReminderTable()
    {
        var builder = new TestSiloBuilder();

        builder.UseInMemoryAdvancedReminderService();

        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(InMemoryReminderTable));
        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(Orleans.AdvancedReminders.IReminderTable));
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}

[TestCategory("Reminders")]
public class AdvancedReminderServiceTests
{
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

        var reminder = await service.RegisterOrUpdateReminder(
            grainId,
            "cron",
            ReminderSchedule.Cron("0 9 * * *"),
            ReminderPriority.Normal,
            MissedReminderAction.Skip);

        Assert.Equal("cron", reminder.ReminderName);
        await reminderTable.Received(1).UpsertRow(Arg.Is<ReminderEntry>(entry =>
            entry.GrainId == grainId
            && entry.ReminderName == "cron"
            && entry.Period == TimeSpan.Zero
            && entry.CronExpression == "0 9 * * *"
            && entry.Priority == ReminderPriority.Normal
            && entry.Action == MissedReminderAction.Skip
            && entry.NextDueUtc != null));
        await jobManager.Received(1).ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
        var request = Assert.IsType<ScheduleJobRequest>(scheduledRequest);
        Assert.Equal("durable-reminder:cron", request.JobName);
        Assert.Equal(dispatcherGrainId, request.Target);
        Assert.Equal(grainId.ToString(), request.Metadata!["grain-id"]);
        Assert.Equal("cron", request.Metadata["reminder-name"]);
        Assert.Equal("etag-1", request.Metadata["etag"]);
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

        var reminder = await service.RegisterOrUpdateReminder(
            grainId,
            "absolute",
            ReminderSchedule.Interval(dueAtUtc, TimeSpan.FromMinutes(5)),
            ReminderPriority.High,
            MissedReminderAction.Notify);

        Assert.Equal("absolute", reminder.ReminderName);
        await reminderTable.Received(1).UpsertRow(Arg.Is<ReminderEntry>(entry =>
            entry.GrainId == grainId
            && entry.ReminderName == "absolute"
            && entry.StartAt == dueAtUtc
            && entry.NextDueUtc == dueAtUtc
            && entry.Period == TimeSpan.FromMinutes(5)
            && entry.Priority == ReminderPriority.High
            && entry.Action == MissedReminderAction.Notify
            && string.IsNullOrEmpty(entry.CronExpression)));
        var request = Assert.IsType<ScheduleJobRequest>(scheduledRequest);
        Assert.Equal("durable-reminder:absolute", request.JobName);
        Assert.Equal("etag-absolute", request.Metadata!["etag"]);
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
            Priority = ReminderPriority.High,
            Action = MissedReminderAction.Notify,
        };
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(grainId, "r").Returns(Task.FromResult(entry));
        var service = CreateService(reminderTable);

        var result = await service.GetReminder(grainId, "r");

        var reminder = Assert.IsAssignableFrom<IGrainReminder>(result);
        Assert.Equal("r", reminder.ReminderName);
        Assert.Equal("0 9 * * *", reminder.CronExpression);
        Assert.Equal("UTC", reminder.CronTimeZone);
        Assert.Equal(ReminderPriority.High, reminder.Priority);
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
                Priority = ReminderPriority.Normal,
                Action = MissedReminderAction.Skip,
            },
            new ReminderEntry
            {
                GrainId = grainId,
                ReminderName = "cron",
                ETag = "etag-b",
                CronExpression = "*/5 * * * *",
                CronTimeZoneId = "UTC",
                Priority = ReminderPriority.High,
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
                Assert.Equal(string.Empty, reminder.CronExpression);
                Assert.Equal(ReminderPriority.Normal, reminder.Priority);
                Assert.Equal(MissedReminderAction.Skip, reminder.Action);
            },
            reminder =>
            {
                Assert.Equal("cron", reminder.ReminderName);
                Assert.Equal("*/5 * * * *", reminder.CronExpression);
                Assert.Equal("UTC", reminder.CronTimeZone);
                Assert.Equal(ReminderPriority.High, reminder.Priority);
                Assert.Equal(MissedReminderAction.FireImmediately, reminder.Action);
            });
    }

    [Fact]
    public async Task UnregisterReminder_WithValidHandle_RemovesReminderUsingETag()
    {
        var grainId = GrainId.Create("test", "remove-valid");
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(grainId, "r").Returns(Task.FromResult(new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "r",
            ETag = "etag-remove",
        }));
        var service = CreateService(reminderTable);
        var reminder = await service.GetReminder(grainId, "r");

        await service.UnregisterReminder(reminder!);

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
    public async Task ProcessDueReminderAsync_WhenReminderIsMissing_Returns()
    {
        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(Arg.Any<GrainId>(), Arg.Any<string>()).Returns(Task.FromResult<ReminderEntry>(null!));

        var service = CreateService(reminderTable);

        await service.ProcessDueReminderAsync(GrainId.Create("test", "missing"), "r", expectedETag: null, CancellationToken.None);

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
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult(entry));

        var service = CreateService(reminderTable);

        await service.ProcessDueReminderAsync(entry.GrainId, entry.ReminderName, expectedETag: "stale", CancellationToken.None);

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
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult(entry));

        var service = CreateService(reminderTable, options: new AdvancedReminderOptions { PollInterval = TimeSpan.FromSeconds(1) });

        await service.ProcessDueReminderAsync(entry.GrainId, entry.ReminderName, expectedETag: entry.ETag, CancellationToken.None);

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
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult(entry));

        var remindable = Substitute.For<AdvancedRemindable>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain(typeof(AdvancedRemindable), entry.GrainId.Key).Returns(remindable);

        var service = CreateService(
            reminderTable,
            options: new AdvancedReminderOptions { PollInterval = TimeSpan.FromSeconds(1) },
            grainFactory: grainFactory);

        await service.ProcessDueReminderAsync(entry.GrainId, entry.ReminderName, expectedETag: entry.ETag, CancellationToken.None);

        await reminderTable.Received(1).RemoveRow(entry.GrainId, entry.ReminderName, entry.ETag);
        Assert.Empty(remindable.ReceivedCalls());
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
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult(entry));
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-interval-2");

        var remindable = Substitute.For<AdvancedRemindable>();
        var dispatcherGrainId = GrainId.Create("sys", "durable-reminder-dispatcher");
        var grainFactory = Substitute.For<IGrainFactory>();
        var dispatcher = CreateDispatcherGrain(dispatcherGrainId);
        grainFactory.GetGrain(typeof(AdvancedRemindable), entry.GrainId.Key).Returns(remindable);
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

        await service.ProcessDueReminderAsync(entry.GrainId, entry.ReminderName, expectedETag: entry.ETag, CancellationToken.None);

        AssertReminderReceived(remindable, "interval", status =>
        {
            Assert.Equal(entry.StartAt, status.FirstTickTime);
            Assert.Equal(entry.Period, status.Period);
            Assert.True(status.CurrentTickTime >= now);
        });
        await reminderTable.Received(1).UpsertRow(Arg.Is<ReminderEntry>(updated =>
            updated.GrainId == entry.GrainId
            && updated.ReminderName == entry.ReminderName
            && updated.LastFireUtc != null
            && updated.NextDueUtc != null
            && updated.NextDueUtc > now
            && updated.Period == entry.Period
            && updated.CronExpression == entry.CronExpression));
        await jobManager.Received(1).ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
        var request = Assert.IsType<ScheduleJobRequest>(scheduledRequest);
        Assert.Equal("durable-reminder:interval", request.JobName);
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
        reminderTable.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult(entry));
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-cron-2");

        var remindable = Substitute.For<AdvancedRemindable>();
        var dispatcherGrainId = GrainId.Create("sys", "durable-reminder-dispatcher");
        var grainFactory = Substitute.For<IGrainFactory>();
        var dispatcher = CreateDispatcherGrain(dispatcherGrainId);
        grainFactory.GetGrain(typeof(AdvancedRemindable), entry.GrainId.Key).Returns(remindable);
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

        await service.ProcessDueReminderAsync(entry.GrainId, entry.ReminderName, expectedETag: entry.ETag, CancellationToken.None);

        AssertReminderReceived(remindable, "cron", status =>
        {
            Assert.Equal(entry.StartAt, status.FirstTickTime);
            Assert.Equal(TimeSpan.Zero, status.Period);
            Assert.True(status.CurrentTickTime >= now);
        });
        await reminderTable.Received(1).UpsertRow(Arg.Is<ReminderEntry>(updated =>
            updated.GrainId == entry.GrainId
            && updated.ReminderName == entry.ReminderName
            && updated.LastFireUtc != null
            && updated.NextDueUtc != null
            && updated.NextDueUtc > now
            && updated.Period == TimeSpan.Zero
            && updated.CronExpression == entry.CronExpression));
        await jobManager.Received(1).ScheduleJobAsync(Arg.Any<ScheduleJobRequest>(), Arg.Any<CancellationToken>());
        var request = Assert.IsType<ScheduleJobRequest>(scheduledRequest);
        Assert.Equal("durable-reminder:cron", request.JobName);
        Assert.Equal(dispatcherGrainId, request.Target);
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

    private static AdvancedReminderService CreateService(
        Orleans.AdvancedReminders.IReminderTable reminderTable,
        AdvancedReminderOptions? options = null,
        ILocalDurableJobManager? jobManager = null,
        IGrainFactory? grainFactory = null)
    {
        jobManager ??= Substitute.For<ILocalDurableJobManager>();
        grainFactory ??= Substitute.For<IGrainFactory>();
        return new AdvancedReminderService(
            reminderTable,
            jobManager,
            grainFactory,
            Options.Create(options ?? new AdvancedReminderOptions()),
            NullLogger<AdvancedReminderService>.Instance);
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

    private static IAdvancedReminderDispatcherGrain CreateDispatcherGrain(GrainId grainId)
        => new TestAdvancedReminderDispatcherGrain(grainId);

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

    private sealed class TestAdvancedReminderDispatcherGrain : IAdvancedReminderDispatcherGrain, IGrainBase
    {
        public TestAdvancedReminderDispatcherGrain(GrainId grainId)
        {
            var context = Substitute.For<IGrainContext>();
            context.GrainId.Returns(grainId);
            GrainContext = context;
        }

        public IGrainContext GrainContext { get; }

        public Task ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
