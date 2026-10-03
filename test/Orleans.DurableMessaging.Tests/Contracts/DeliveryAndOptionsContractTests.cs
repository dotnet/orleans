using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
using Orleans.DurableMessaging.Tests.Functional;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Timers;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DeliveryAndOptionsContractTests
{
    [Fact]
    public void RetentionTimeArithmetic_HandlesMaximumDurationsWithoutOverflow()
    {
        var timeType = typeof(IDurableInbox).Assembly.GetType(
            "Orleans.DurableMessaging.DurableMessagingTime",
            throwOnError: true)!;
        var isExpired = timeType.GetMethod(
            "IsExpired",
            BindingFlags.Static | BindingFlags.Public)!;
        var addClamped = timeType.GetMethod(
            "AddClamped",
            BindingFlags.Static | BindingFlags.Public)!;
        var timestamp = DateTimeOffset.MaxValue - TimeSpan.FromTicks(1);

        Assert.False((bool)isExpired.Invoke(
            null,
            [DateTimeOffset.MaxValue, timestamp, TimeSpan.MaxValue])!);
        var fullDateTimeRange = TimeSpan.FromTicks(
            DateTimeOffset.MaxValue.UtcTicks - DateTimeOffset.MinValue.UtcTicks);
        Assert.True((bool)isExpired.Invoke(
            null,
            [DateTimeOffset.MaxValue, DateTimeOffset.MinValue, fullDateTimeRange])!);
        Assert.False((bool)isExpired.Invoke(
            null,
            [DateTimeOffset.MaxValue, DateTimeOffset.MinValue, TimeSpan.MaxValue])!);
        Assert.False((bool)isExpired.Invoke(
            null,
            [DateTimeOffset.MinValue, DateTimeOffset.MaxValue, TimeSpan.FromTicks(1)])!);
        Assert.Equal(
            DateTimeOffset.MaxValue,
            (DateTimeOffset)addClamped.Invoke(null, [timestamp, TimeSpan.MaxValue])!);
    }

    [Fact]
    public void DeadLetterCompaction_SaturatesMaximumRetentionWithoutOverflow()
    {
        var retentionType = typeof(IDurableInbox).Assembly.GetType(
            "Orleans.DurableMessaging.DurableDeadLetterRetention",
            throwOnError: true)!;
        var compact = retentionType.GetMethod(
            "Compact",
            BindingFlags.Static | BindingFlags.Public)!
            .MakeGenericMethod(typeof(string), typeof(DateTimeOffset));
        var entries = new Dictionary<string, DateTimeOffset>
        {
            ["oldest"] = DateTimeOffset.MinValue,
            ["newest"] = DateTimeOffset.MaxValue
        };

        var removed = (bool)compact.Invoke(
            null,
            [
                entries,
                DateTimeOffset.MaxValue,
                TimeSpan.MaxValue,
                int.MaxValue,
                (Func<DateTimeOffset, DateTimeOffset>)(static timestamp => timestamp),
                0
            ])!;

        Assert.False(removed);
        Assert.Equal(2, entries.Count);

        var fullDateTimeRange = TimeSpan.FromTicks(
            DateTimeOffset.MaxValue.UtcTicks - DateTimeOffset.MinValue.UtcTicks);
        removed = (bool)compact.Invoke(
            null,
            [
                entries,
                DateTimeOffset.MaxValue,
                fullDateTimeRange,
                int.MaxValue,
                (Func<DateTimeOffset, DateTimeOffset>)(static timestamp => timestamp),
                0
            ])!;

        Assert.True(removed);
        Assert.DoesNotContain("oldest", entries);
        Assert.Contains("newest", entries);
    }

    [Fact]
    public void DeliveryResult_EachFactory_PreservesStatusAndPayload()
    {
        var routeMissing = DeliveryResult.RouteNotFound("orders/missing");
        var deadLettered = DeliveryResult.DeadLettered("poison body");

        Assert.Equal(DeliveryStatus.Accepted, DeliveryResult.Accepted().Status);
        Assert.Equal(DeliveryStatus.Duplicate, DeliveryResult.Duplicate().Status);
        Assert.Equal(DeliveryStatus.Backpressured, DeliveryResult.Backpressured().Status);
        Assert.Equal(DeliveryStatus.RouteNotFound, routeMissing.Status);
        Assert.Equal("No handler for route 'orders/missing'", routeMissing.Message);
        Assert.Equal(DeliveryStatus.DeadLettered, deadLettered.Status);
        Assert.Equal("poison body", deadLettered.Message);
    }

    [Fact]
    public void DeliveryStatus_AllValues_HaveStableDistinctValues()
    {
        Assert.Equal(
            [
                DeliveryStatus.Accepted,
                DeliveryStatus.Duplicate,
                DeliveryStatus.Backpressured,
                DeliveryStatus.RouteNotFound,
                DeliveryStatus.DeadLettered
            ],
            Enum.GetValues<DeliveryStatus>());
        Assert.Equal([0, 1, 2, 3, 6], Enum.GetValues<DeliveryStatus>().Select(static value => (int)value));
    }

    [Fact]
    public void Validate_DefaultOptions_SucceedsAndExposesDocumentedDefaults()
    {
        var options = new DurableInboxOptions();

        options.Validate();

        Assert.Equal(1000, options.MaxCapacity);
        Assert.Equal(TimeSpan.FromDays(7), options.DeduplicationWindow);
        Assert.Equal(TimeSpan.FromDays(1), options.MaxOutboxRetryAge);
        Assert.Equal(TimeSpan.FromDays(30), options.DeadLetterRetentionPeriod);
        Assert.Equal(5, options.MaxProcessingAttempts);
        Assert.Equal(100, options.MaxDeliveryAttempts);
        Assert.Equal(1000, options.MaxRetainedDeadLetters);
        Assert.Equal(32, options.InboxBatchSize);
        Assert.Equal(32, options.OutboxBatchSize);
    }

    [Fact]
    public void Validate_EachCapacityRetryDeadLetterAndBatchBoundary_EnforcesContract()
    {
        var invalidCases = new (string Parameter, Action<DurableInboxOptions> Mutate)[]
        {
            (nameof(DurableInboxOptions.MaxCapacity), options => options.MaxCapacity = 0),
            (nameof(DurableInboxOptions.DeduplicationWindow), options => options.DeduplicationWindow = TimeSpan.Zero),
            (nameof(DurableInboxOptions.BackpressureRetryDelay), options => options.BackpressureRetryDelay = TimeSpan.Zero),
            (nameof(DurableInboxOptions.BackpressureRetryDelay), options => options.BackpressureRetryDelay = TimeSpan.MaxValue),
            (nameof(DurableInboxOptions.MaxProcessingAttempts), options => options.MaxProcessingAttempts = 0),
            (nameof(DurableInboxOptions.MaxDeliveryAttempts), options => options.MaxDeliveryAttempts = 0),
            (nameof(DurableInboxOptions.MaxOutboxRetryAge), options => options.MaxOutboxRetryAge = TimeSpan.Zero),
            (nameof(DurableInboxOptions.DeadLetterRetentionPeriod), options => options.DeadLetterRetentionPeriod = TimeSpan.Zero),
            (nameof(DurableInboxOptions.MaxRetainedDeadLetters), options => options.MaxRetainedDeadLetters = 0),
            (nameof(DurableInboxOptions.InboxBatchSize), options => options.InboxBatchSize = 0),
            (nameof(DurableInboxOptions.OutboxBatchSize), options => options.OutboxBatchSize = 0),
        };

        foreach (var (parameter, mutate) in invalidCases)
        {
            var options = new DurableInboxOptions();
            mutate(options);
            var exception = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
            Assert.Equal(parameter, exception.ParamName);
        }
    }

    [Fact]
    public void Validate_MaxOutboxRetryAgeNotLessThanDeduplicationWindow_FailsAtBoundaryAndAbove()
    {
        foreach (var retryAge in new[] { TimeSpan.FromHours(2), TimeSpan.FromHours(3) })
        {
            var options = new DurableInboxOptions
            {
                DeduplicationWindow = TimeSpan.FromHours(2),
                MaxOutboxRetryAge = retryAge,
            };

            var exception = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
            Assert.Equal(nameof(DurableInboxOptions.MaxOutboxRetryAge), exception.ParamName);
            Assert.Contains("less than DeduplicationWindow", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InboxDispose_CancelsWorkOnceAndSupportsRepeatedDisposal()
    {
        var assembly = typeof(IDurableInbox).Assembly;
        var extensionType = assembly.GetType("Orleans.DurableMessaging.DurableInboxExtension", throwOnError: true)!;
        var coordinatorType = assembly.GetType("Orleans.DurableMessaging.DurableMessagingPumpCoordinator", throwOnError: true)!;
        var extension = (IDisposable)RuntimeHelpers.GetUninitializedObject(extensionType);
        var coordinator = Activator.CreateInstance(coordinatorType)!;
        var results = Activator.CreateInstance(assembly.GetType("Orleans.DurableMessaging.DurableMessagingPumpResults", throwOnError: true)!, nonPublic: true)!;
        extensionType.GetField("_pumpResults", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(extension, results);
        using var shutdown = new CancellationTokenSource();
        var token = shutdown.Token;
        var cancellationCount = 0;
        using var registration = token.Register(() => cancellationCount++);
        extensionType.GetField("_shutdownCts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(extension, shutdown);
        extensionType.GetField("_pumpCoordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(extension, coordinator);
        object?[] acquireArguments = ["owner", token, null];
        Assert.True((bool)coordinatorType.GetMethod("TryAcquire")!.Invoke(coordinator, acquireArguments)!);

        extension.Dispose();

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(1, cancellationCount);
        Assert.False((bool)coordinatorType.GetMethod("IsCurrent")!.Invoke(coordinator, [acquireArguments[2]])!);
        Assert.Throws<ObjectDisposedException>(() => shutdown.Token);

        extension.Dispose();

        Assert.Equal(1, cancellationCount);
    }

    [Fact]
    public async Task InboxLifecycleStart_ObservesPreCanceledLifecycleToken()
    {
        var extensionType = typeof(IDurableInbox).Assembly.GetType(
            "Orleans.DurableMessaging.DurableInboxExtension",
            throwOnError: true)!;
        var extension = (ILifecycleObserver)RuntimeHelpers.GetUninitializedObject(extensionType);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => extension.OnStart(cancellation.Token));
    }

    [Fact]
    public async Task InboxLifecycleStart_CancellationInterruptsBlockedResume()
    {
        var builder = InboxStateManagerBoundaryTests.CreateBuilder("orleans-binary");
        var id = new JournalId("lifecycle-start/" + Guid.NewGuid().ToString("N"));
        builder.Services.AddScoped<IJournaledStateManager>(sp =>
            sp.GetRequiredService<IJournaledStateManagerFactory>().CreateStandalone(id));
        builder.Services.AddScoped<IGrainContext>(sp =>
        {
            var context = Substitute.For<IGrainContext>();
            context.GrainId.Returns(GrainId.Create("lifecycle-start", "blocked"));
            context.GrainInstance.Returns(Substitute.For<IDurableMessagingGrain>());
            context.ActivationServices.Returns(sp);
            context.ObservableLifecycle.Returns(Substitute.For<IGrainLifecycle>());
            return context;
        });
        var jobs = Substitute.For<ILocalDurableJobManager>();
        var timers = Substitute.For<ITimerRegistry>();
        builder.Services.AddSingleton(jobs);
        builder.Services.AddSingleton(timers);
        builder.Services.AddSingleton(Substitute.For<IDurableJobHandlerRegistry>());
        var instrumentsType = ReceiverTestServices.GetImplementationType("DurableMessagingInstruments");
        builder.Services.AddSingleton(instrumentsType,
            instrumentsType.GetMethod("CreateForDirectConstruction", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!);
        ReceiverTestServices.Add(builder.Services, static _ => { });
        await using var provider = builder.Services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var extensionType = ReceiverTestServices.GetImplementationType("DurableInboxExtension");
        var extension = (ILifecycleObserver)scope.ServiceProvider.GetRequiredService(extensionType);
        var owner = scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
        await owner.InitializeAsync(TestContext.Current.CancellationToken);
        var gate = (SemaphoreSlim)extensionType.GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(extension)!;
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            using var cancellation = new CancellationTokenSource();
            var start = extension.OnStart(cancellation.Token);
            Assert.False(start.IsCompleted);
            cancellation.Cancel();

            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            Assert.Equal(cancellation.Token, canceled.CancellationToken);
            Assert.Equal(0, gate.CurrentCount);
            Assert.Empty(jobs.ReceivedCalls());
            Assert.Empty(timers.ReceivedCalls());
            Assert.Empty(scope.ServiceProvider.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>(
                "__orleans.durable-messaging.inbox"));
        }
        finally
        {
            gate.Release();
        }
    }
}
