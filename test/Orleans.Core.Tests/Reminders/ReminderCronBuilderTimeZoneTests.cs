using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Reminders.Cron.Internal;
using Orleans.Runtime;
using Orleans.Timers;
using Xunit;

namespace NonSilo.Tests.Reminders;

[TestCategory("Reminders")]
public class ReminderCronBuilderTimeZoneTests
{
    [Fact]
    public void Builder_DefaultTimeZone_IsUtc()
    {
        var builder = ReminderCronBuilder.DailyAt(9, 0);

        Assert.Equal(TimeZoneInfo.Utc.Id, builder.TimeZone.Id);
    }

    [Fact]
    public void Builder_InTimeZone_WithTimeZoneInfo_UsesLocalScheduleAndReturnsUtc()
    {
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(TimeZoneTestHelper.GetCentralEuropeanTimeZone());
        var fromUtc = new DateTime(2026, 1, 1, 6, 30, 0, DateTimeKind.Utc);

        var next = builder.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Builder_InTimeZone_WithTimeZoneInfo_GetOccurrences_UsesLocalScheduleAndReturnsUtc()
    {
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(TimeZoneTestHelper.GetCentralEuropeanTimeZone());
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);

        var occurrences = builder.GetOccurrences(fromUtc, toUtc).ToArray();

        Assert.Equal(
            [
                new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 2, 8, 0, 0, DateTimeKind.Utc),
            ],
            occurrences);
    }

    [Fact]
    public void Builder_InTimeZone_WithCentralEurope_NineAmLocalStillWorksWhenServerIsUtc()
    {
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(TimeZoneTestHelper.GetCentralEuropeanTimeZone());
        var fromUtc = new DateTime(2025, 3, 29, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2025, 4, 2, 0, 0, 0, DateTimeKind.Utc);

        var occurrences = builder.GetOccurrences(fromUtc, toUtc).ToArray();

        Assert.Equal(
            [
                new DateTime(2025, 3, 29, 8, 0, 0, DateTimeKind.Utc), // 09:00 CET
                new DateTime(2025, 3, 30, 7, 0, 0, DateTimeKind.Utc), // 09:00 CEST after EU DST
                new DateTime(2025, 3, 31, 7, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 4, 1, 7, 0, 0, DateTimeKind.Utc),
            ],
            occurrences);
    }

    [Fact]
    public void Builder_InTimeZone_WithDubaiTimeZone_HasStableUtcTriggerTime()
    {
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(TimeZoneTestHelper.GetDubaiTimeZone());
        var winterFromUtc = new DateTime(2026, 1, 15, 4, 30, 0, DateTimeKind.Utc);
        var summerFromUtc = new DateTime(2026, 7, 15, 4, 30, 0, DateTimeKind.Utc);

        var winterNext = builder.GetNextOccurrence(winterFromUtc);
        var summerNext = builder.GetNextOccurrence(summerFromUtc);

        Assert.Equal(new DateTime(2026, 1, 15, 5, 0, 0, DateTimeKind.Utc), winterNext);
        Assert.Equal(new DateTime(2026, 7, 15, 5, 0, 0, DateTimeKind.Utc), summerNext);
    }

    [Fact]
    public void Builder_FromExpression_WithTimeZoneParameter_UsesProvidedZone()
    {
        var zone = TimeZoneTestHelper.GetNepalTimeZone();
        var builder = ReminderCronBuilder.FromExpression("0 9 * * *", zone);
        var fromUtc = new DateTime(2026, 1, 15, 3, 0, 0, DateTimeKind.Utc);

        var next = builder.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 15, 3, 15, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Builder_InTimeZone_WithIndiaAndNepalTimeZones_UsesHalfAndQuarterHourOffsets()
    {
        var fromUtc = new DateTime(2026, 1, 15, 3, 0, 0, DateTimeKind.Utc);
        var indiaBuilder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(TimeZoneTestHelper.GetIndiaTimeZone());
        var nepalBuilder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(TimeZoneTestHelper.GetNepalTimeZone());

        var indiaNext = indiaBuilder.GetNextOccurrence(fromUtc);
        var nepalNext = nepalBuilder.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 15, 3, 30, 0, DateTimeKind.Utc), indiaNext);
        Assert.Equal(new DateTime(2026, 1, 15, 3, 15, 0, DateTimeKind.Utc), nepalNext);
    }

    [Fact]
    public void Builder_InTimeZone_WithLordHoweTimeZone_ReflectsThirtyMinuteDstShift()
    {
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(TimeZoneTestHelper.GetLordHoweTimeZone());
        var summerFromUtc = new DateTime(2026, 1, 14, 21, 45, 0, DateTimeKind.Utc);
        var winterFromUtc = new DateTime(2026, 7, 14, 22, 15, 0, DateTimeKind.Utc);

        var summerNext = builder.GetNextOccurrence(summerFromUtc);
        var winterNext = builder.GetNextOccurrence(winterFromUtc);

        Assert.Equal(new DateTime(2026, 1, 14, 22, 0, 0, DateTimeKind.Utc), summerNext);
        Assert.Equal(new DateTime(2026, 7, 14, 22, 30, 0, DateTimeKind.Utc), winterNext);
    }

    [Fact]
    public void Builder_InTimeZone_WithUsEasternAcrossSpringForward_PreservesNineAmLocal()
    {
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(TimeZoneTestHelper.GetUsEasternTimeZone());
        var fromUtc = new DateTime(2025, 3, 7, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2025, 3, 12, 0, 0, 0, DateTimeKind.Utc);

        var occurrences = builder.GetOccurrences(fromUtc, toUtc).ToArray();

        Assert.Equal(
            [
                new DateTime(2025, 3, 7, 14, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 3, 8, 14, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 3, 9, 13, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 3, 10, 13, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 3, 11, 13, 0, 0, DateTimeKind.Utc),
            ],
            occurrences);
    }

    [Fact]
    public void Builder_InTimeZone_WithUsEasternAcrossFallBack_PreservesNineAmLocal()
    {
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(TimeZoneTestHelper.GetUsEasternTimeZone());
        var fromUtc = new DateTime(2025, 10, 31, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2025, 11, 4, 0, 0, 0, DateTimeKind.Utc);

        var occurrences = builder.GetOccurrences(fromUtc, toUtc).ToArray();

        Assert.Equal(
            [
                new DateTime(2025, 10, 31, 13, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 11, 1, 13, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 11, 2, 14, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 11, 3, 14, 0, 0, DateTimeKind.Utc),
            ],
            occurrences);
    }

    [Fact]
    public void Builder_InTimeZone_WithTimeZoneId_UsesProvidedZone()
    {
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(TimeZoneInfo.Utc.Id);

        Assert.Equal(TimeZoneInfo.Utc.Id, builder.TimeZone.Id);
    }

    [Fact]
    public void Builder_InTimeZone_WithAlternatePlatformId_UsesEquivalentZone()
    {
        var alternateZoneId = TimeZoneTestHelper.GetCentralEuropeanAlternateTimeZoneId();
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(alternateZoneId);
        var fromUtc = new DateTime(2026, 1, 1, 7, 30, 0, DateTimeKind.Utc);

        var next = builder.GetNextOccurrence(fromUtc);

        Assert.Equal(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Builder_InTimeZone_WithUnknownTimeZoneId_Throws()
    {
        var builder = ReminderCronBuilder.DailyAt(9, 0);

        Assert.Throws<TimeZoneNotFoundException>(() => builder.InTimeZone("Unknown/TimeZone"));
    }

    [Fact]
    public void Builder_ToCronExpression_WithNonUtcTimeZone_ReturnsExpression()
    {
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(TimeZoneTestHelper.GetCentralEuropeanTimeZone());

        var expression = builder.ToCronExpression();

        Assert.Equal("0 9 * * *", expression.ToExpressionString());
    }

    [Fact]
    public void Builder_FromExpression_SingleParameterOverloadExists()
    {
        var method = typeof(ReminderCronBuilder).GetMethod(
            nameof(ReminderCronBuilder.FromExpression),
            [typeof(string)]);

        Assert.NotNull(method);
    }

    [Fact]
    public async Task RegistryRegistrationExtensions_WithNonUtcBuilder_DelegatesEncodedSchedule()
    {
        var registry = Substitute.For<IReminderRegistry>();
        var grainId = GrainId.Create("test", "non-utc-builder-registry");
        var reminder = Substitute.For<IGrainReminder>();
        var zone = TimeZoneTestHelper.GetCentralEuropeanTimeZone();
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(zone);
        var expectedTimeZoneId = ReminderCronSchedule.NormalizeTimeZoneIdForStorage(zone);
        registry.RegisterOrUpdateReminder(grainId, "r", Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(reminder));

        var result = await registry.RegisterOrUpdateReminder(grainId, "r", builder);

        Assert.Same(reminder, result);
        await registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            "0 9 * * *",
            expectedTimeZoneId);
    }

    [Fact]
    public async Task ServiceRegistrationExtensions_WithNonUtcBuilder_DelegatesEncodedSchedule()
    {
        var service = Substitute.For<IReminderService>();
        var grainId = GrainId.Create("test", "non-utc-builder-service");
        var reminder = Substitute.For<IGrainReminder>();
        var zone = TimeZoneTestHelper.GetCentralEuropeanTimeZone();
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(zone);
        var expectedTimeZoneId = ReminderCronSchedule.NormalizeTimeZoneIdForStorage(zone);
        service.RegisterOrUpdateReminder(grainId, "r", Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(reminder));

        var result = await service.RegisterOrUpdateReminder(grainId, "r", builder);

        Assert.Same(reminder, result);
        await service.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            "0 9 * * *",
            expectedTimeZoneId);
    }

    [Fact]
    public async Task GrainRegistrationExtensions_WithNonUtcBuilder_DelegatesEncodedSchedule()
    {
        var grainId = GrainId.Create("test", "non-utc-builder-grain");
        var registry = Substitute.For<IReminderRegistry>();
        var reminder = Substitute.For<IGrainReminder>();
        var zone = TimeZoneTestHelper.GetCentralEuropeanTimeZone();
        var expectedTimeZoneId = ReminderCronSchedule.NormalizeTimeZoneIdForStorage(zone);
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(zone);
        registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                "0 9 * * *",
                ReminderPriority.Normal,
                MissedReminderAction.Skip,
                expectedTimeZoneId)
            .Returns(Task.FromResult(reminder));
        var grain = CreateRemindableGrain(grainId, registry);

        var result = await grain.RegisterOrUpdateReminder("r", builder);

        Assert.Same(reminder, result);
        await registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            "0 9 * * *",
            ReminderPriority.Normal,
            MissedReminderAction.Skip,
            expectedTimeZoneId);
    }

    [Fact]
    public async Task GrainRegistrationExtensions_WithExpressionAndOptionalTimeZone_DelegatesEncodedSchedule()
    {
        var grainId = GrainId.Create("test", "non-utc-expression-grain");
        var registry = Substitute.For<IReminderRegistry>();
        var reminder = Substitute.For<IGrainReminder>();
        var zone = TimeZoneTestHelper.GetCentralEuropeanTimeZone();
        var expectedTimeZoneId = ReminderCronSchedule.NormalizeTimeZoneIdForStorage(zone);
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                "0 9 * * *",
                ReminderPriority.Normal,
                MissedReminderAction.Skip,
                expectedTimeZoneId)
            .Returns(Task.FromResult(reminder));
        var grain = CreateRemindableGrain(grainId, registry);

        var result = await grain.RegisterOrUpdateReminder("r", expression, timeZone: zone);

        Assert.Same(reminder, result);
        await registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            "0 9 * * *",
            ReminderPriority.Normal,
            MissedReminderAction.Skip,
            expectedTimeZoneId);
    }

    [Fact]
    public async Task RegistryRegistrationExtensions_WithTimeZoneParameter_DelegatesEncodedSchedule()
    {
        var registry = Substitute.For<IReminderRegistry>();
        var grainId = GrainId.Create("test", "timezone-parameter-registry");
        var reminder = Substitute.For<IGrainReminder>();
        var zone = TimeZoneTestHelper.GetCentralEuropeanTimeZone();
        var expectedTimeZoneId = ReminderCronSchedule.NormalizeTimeZoneIdForStorage(zone);
        registry.RegisterOrUpdateReminder(grainId, "r", Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(reminder));

        var result = await registry.RegisterOrUpdateReminder(grainId, "r", "0 9 * * *", zone);

        Assert.Same(reminder, result);
        await registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            "0 9 * * *",
            expectedTimeZoneId);
    }

    [Fact]
    public async Task RegistryRegistrationExtensions_WithNullOptionalTimeZone_DelegatesUtcSchedule()
    {
        var registry = Substitute.For<IReminderRegistry>();
        var grainId = GrainId.Create("test", "timezone-parameter-registry-null");
        var reminder = Substitute.For<IGrainReminder>();
        registry.RegisterOrUpdateReminder(grainId, "r", Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(reminder));

        var result = await registry.RegisterOrUpdateReminder(grainId, "r", "0 9 * * *", timeZone: null);

        Assert.Same(reminder, result);
        await registry.Received(1).RegisterOrUpdateReminder(grainId, "r", "0 9 * * *", null);
    }

    [Fact]
    public async Task RegistryRegistrationExtensions_WithExpressionAndOptionalTimeZone_DelegatesEncodedSchedule()
    {
        var registry = Substitute.For<IReminderRegistry>();
        var grainId = GrainId.Create("test", "timezone-parameter-registry-expression");
        var reminder = Substitute.For<IGrainReminder>();
        var zone = TimeZoneTestHelper.GetCentralEuropeanTimeZone();
        var expectedTimeZoneId = ReminderCronSchedule.NormalizeTimeZoneIdForStorage(zone);
        var expression = ReminderCronExpression.Parse("0 9 * * *");
        registry.RegisterOrUpdateReminder(grainId, "r", Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(reminder));

        var result = await registry.RegisterOrUpdateReminder(grainId, "r", expression, timeZone: zone);

        Assert.Same(reminder, result);
        await registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            "0 9 * * *",
            expectedTimeZoneId);
    }

    [Fact]
    public async Task ServiceRegistrationExtensions_WithTimeZoneParameter_DelegatesEncodedSchedule()
    {
        var service = Substitute.For<IReminderService>();
        var grainId = GrainId.Create("test", "timezone-parameter-service");
        var reminder = Substitute.For<IGrainReminder>();
        var zone = TimeZoneTestHelper.GetCentralEuropeanTimeZone();
        var expectedTimeZoneId = ReminderCronSchedule.NormalizeTimeZoneIdForStorage(zone);
        service.RegisterOrUpdateReminder(grainId, "r", Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(reminder));

        var result = await service.RegisterOrUpdateReminder(grainId, "r", "0 9 * * *", zone);

        Assert.Same(reminder, result);
        await service.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            "0 9 * * *",
            expectedTimeZoneId);
    }

    [Fact]
    public void GrainReminderExtensions_NonCronOverloads_DoNotExposeTimeZoneParameter()
    {
        var methods = typeof(GrainReminderExtensions)
            .GetMethods()
            .Where(m => m.Name == nameof(GrainReminderExtensions.RegisterOrUpdateReminder));

        Assert.DoesNotContain(
            methods,
            static method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(TimeZoneInfo)));
    }

    [Fact]
    public void GrainReminderCronExtensions_TimeZoneOverloads_AreCronOnly()
    {
        var methodsWithTimeZone = typeof(GrainReminderCronExtensions)
            .GetMethods()
            .Where(m => m.Name == nameof(GrainReminderCronExtensions.RegisterOrUpdateReminder))
            .Where(static method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(TimeZoneInfo)))
            .ToArray();

        Assert.NotEmpty(methodsWithTimeZone);
        Assert.All(
            methodsWithTimeZone,
            static method => Assert.Contains(
                method.GetParameters(),
                static parameter => parameter.ParameterType == typeof(string)
                    || parameter.ParameterType == typeof(ReminderCronExpression)
                    || parameter.ParameterType == typeof(ReminderCronBuilder)));
    }

    private static IGrainBase CreateRemindableGrain(GrainId grainId, IReminderRegistry registry)
    {
        var services = new ServiceCollection().AddSingleton(registry).BuildServiceProvider();
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(grainId);
        context.ActivationServices.Returns(services);

        var grain = Substitute.For<IGrainBase, IRemindable>();
        grain.GrainContext.Returns(context);
        return grain;
    }
}
