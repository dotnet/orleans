using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Dashboard;
using Orleans.Dashboard.Core;
using Orleans.Dashboard.Implementation;
using Orleans.Dashboard.Implementation.Details;
using Orleans.Dashboard.Metrics;
using Orleans.Dashboard.Metrics.Details;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Services;
using Xunit;

namespace UnitTests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Dashboard")]
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddDashboard_SiloBuilder_RegistersExpectedImplementationsLifetimesAndSharedIdentities()
    {
        var builder = new TestSiloBuilder();

        var returnedBuilder = builder.AddDashboard();

        Assert.Same(builder, returnedBuilder);
        AssertTypeDescriptor<IDashboardClient, DashboardClient>(builder.Services);
        AssertTypeDescriptor<ISiloGrainClient, SiloGrainClient>(builder.Services);
        AssertTypeDescriptor<IGrainProfiler, GrainProfiler>(builder.Services);
        AssertTypeDescriptor<IIncomingGrainCallFilter, GrainProfilerFilter>(builder.Services);
        AssertTypeDescriptor<EmbeddedAssetProvider, EmbeddedAssetProvider>(builder.Services);
        AssertTypeDescriptor<DashboardTelemetryExporter, DashboardTelemetryExporter>(builder.Services);
        AssertTypeDescriptor<DashboardLogger, DashboardLogger>(builder.Services);
        AssertTypeDescriptor<SiloStatusOracleSiloDetailsProvider, SiloStatusOracleSiloDetailsProvider>(builder.Services);
        AssertTypeDescriptor<MembershipTableSiloDetailsProvider, MembershipTableSiloDetailsProvider>(builder.Services);

        var hostedService = Assert.Single(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(DashboardHost));
        Assert.Equal(ServiceLifetime.Singleton, hostedService.Lifetime);

        var grainService = Assert.Single(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(IGrainService));
        Assert.Equal(ServiceLifetime.Singleton, grainService.Lifetime);
        Assert.NotNull(grainService.ImplementationFactory);

        var loggerProvider = Assert.Single(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(ILoggerProvider));
        Assert.Equal(ServiceLifetime.Singleton, loggerProvider.Lifetime);
        Assert.NotNull(loggerProvider.ImplementationFactory);

        var detailsProvider = Assert.Single(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(ISiloDetailsProvider));
        Assert.Equal(ServiceLifetime.Singleton, detailsProvider.Lifetime);
        Assert.NotNull(detailsProvider.ImplementationFactory);

        Assert.DoesNotContain(builder.Services, descriptor =>
            descriptor.Lifetime is ServiceLifetime.Scoped or ServiceLifetime.Transient
            && IsDashboardRegistration(descriptor.ServiceType));

        using var provider = builder.Services.BuildServiceProvider();
        Assert.Same(
            provider.GetRequiredService<DashboardLogger>(),
            Assert.IsType<DashboardLogger>(provider.GetRequiredService<ILoggerProvider>()));
        Assert.Equal(
            GrainProfilerFilter.DefaultGrainMethodFormatter,
            provider.GetRequiredService<GrainProfilerFilter.GrainMethodFormatterDelegate>());
    }

    [Fact]
    public void AddDashboard_SiloBuilder_CalledTwice_ComposesOptionDelegatesInOrder()
    {
        var builder = new TestSiloBuilder();
        builder.AddDashboard(options =>
        {
            options.CounterUpdateIntervalMs = 2_500;
            options.HistoryLength = 12;
        });

        builder.AddDashboard(options =>
        {
            options.HistoryLength = 24;
            options.HideTrace = true;
        });

        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DashboardOptions>>().Value;
        Assert.Equal(2_500, options.CounterUpdateIntervalMs);
        Assert.Equal(24, options.HistoryLength);
        Assert.True(options.HideTrace);
        Assert.Single(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(DashboardHost));
        Assert.Single(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(GrainProfilerFilter.GrainMethodFormatterDelegate));
    }

    [Fact]
    public async Task AddOrleansDashboardForSiloCore_WithMembershipTable_SelectsMembershipProviderAndForwardsDetailedHostRequest()
    {
        var address = SiloAddress.New(IPAddress.Loopback, 11_111, 42);
        var startTime = new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var aliveTime = startTime.AddMinutes(2);
        var membershipEntry = new MembershipEntry
        {
            SiloAddress = address,
            Status = SiloStatus.Active,
            ProxyPort = 30_000,
            HostName = "dashboard-host",
            SiloName = "dashboard-silo",
            RoleName = "worker",
            UpdateZone = 7,
            FaultZone = 9,
            StartTime = startTime,
            IAmAliveTime = aliveTime,
        };
        bool? onlyActive = null;
        var managementGrain = CreateProxy<IManagementGrain>((method, arguments) =>
        {
            if (method.Name == nameof(IManagementGrain.GetDetailedHosts))
            {
                onlyActive = Assert.IsType<bool>(arguments![0]);
                return Task.FromResult(new[] { membershipEntry });
            }

            throw new NotSupportedException(method.Name);
        });
        var grainFactory = CreateProxy<IGrainFactory>((method, _) =>
        {
            if (method.IsGenericMethod
                && method.Name == nameof(IGrainFactory.GetGrain)
                && method.GetGenericArguments()[0] == typeof(IManagementGrain))
            {
                return managementGrain;
            }

            throw new NotSupportedException(method.Name);
        });
        var membershipTableCalls = 0;
        var membershipTable = CreateProxy<IMembershipTable>((method, _) =>
        {
            membershipTableCalls++;
            throw new NotSupportedException(method.Name);
        });
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton(grainFactory);
        services.AddSingleton(membershipTable);
        services.AddOrleansDashboardForSiloCore();

        using var provider = services.BuildServiceProvider();
        var selectedProvider = provider.GetRequiredService<ISiloDetailsProvider>();
        var result = Assert.Single(await selectedProvider.GetSiloDetails());

        Assert.IsType<MembershipTableSiloDetailsProvider>(selectedProvider);
        Assert.True(onlyActive);
        Assert.Equal(0, membershipTableCalls);
        Assert.Equal(address.ToParsableString(), result.SiloAddress);
        Assert.Equal("dashboard-host", result.HostName);
        Assert.Equal("dashboard-silo", result.SiloName);
        Assert.Equal("worker", result.RoleName);
        Assert.Equal(30_000, result.ProxyPort);
        Assert.Equal(7, result.UpdateZone);
        Assert.Equal(9, result.FaultZone);
        Assert.Equal("2025-02-03T04:05:06.000Z", result.StartTime);
        Assert.Equal("2025-02-03T04:07:06.000Z", result.IAmAliveTime);
        Assert.Equal(SiloStatus.Active, result.SiloStatus);
        Assert.Equal("Active", result.Status);
    }

    [Fact]
    public async Task AddOrleansDashboardForSiloCore_WithoutMembershipTable_SelectsStatusOracleProviderAndMapsActiveSilos()
    {
        var activeAddress = SiloAddress.New(IPAddress.Parse("127.0.0.2"), 11_112, 43);
        var deadAddress = SiloAddress.New(IPAddress.Parse("127.0.0.3"), 11_113, 44);
        var statusOracle = new TestSiloStatusOracle(new Dictionary<SiloAddress, SiloStatus>
        {
            [activeAddress] = SiloStatus.Active,
            [deadAddress] = SiloStatus.Dead,
        });
        var services = new ServiceCollection();
        services.AddSingleton<ISiloStatusOracle>(statusOracle);
        services.AddOrleansDashboardForSiloCore();

        using var provider = services.BuildServiceProvider();
        var selectedProvider = provider.GetRequiredService<ISiloDetailsProvider>();
        var result = Assert.Single(await selectedProvider.GetSiloDetails());

        Assert.IsType<SiloStatusOracleSiloDetailsProvider>(selectedProvider);
        Assert.True(statusOracle.LastOnlyActive);
        Assert.Equal(activeAddress.ToParsableString(), result.SiloAddress);
        Assert.Equal(activeAddress.ToParsableString(), result.SiloName);
        Assert.Equal(SiloStatus.Active, result.SiloStatus);
        Assert.Equal("Active", result.Status);
    }

    [Fact]
    public void AddOrleansDashboardForSiloCore_ProfilerLifecycleRegistrationAliasesProfilerInstance()
    {
        var services = new ServiceCollection();
        services.AddOrleansDashboardForSiloCore();
        var lifecycleDescriptor = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(ILifecycleParticipant<ISiloLifecycle>));
        Assert.Equal(ServiceLifetime.Singleton, lifecycleDescriptor.Lifetime);
        Assert.NotNull(lifecycleDescriptor.ImplementationFactory);

        var profiler = new TestProfiler();
        IServiceCollection aliasServices = new ServiceCollection();
        aliasServices.AddSingleton<IGrainProfiler>(profiler);
        aliasServices.Add(lifecycleDescriptor);

        using var provider = aliasServices.BuildServiceProvider();
        Assert.Same(profiler, provider.GetRequiredService<ILifecycleParticipant<ISiloLifecycle>>());
    }

    [Fact]
    public async Task AddOrleansDashboardForSiloCore_RegisteredCallFilter_InvokesAndProfilesOnce()
    {
        var registrations = new ServiceCollection();
        registrations.AddOrleansDashboardForSiloCore();
        var filterDescriptor = Assert.Single(
            registrations,
            descriptor => descriptor.ServiceType == typeof(IIncomingGrainCallFilter));
        var profiler = new TestProfiler();
        var formatterCalls = 0;
        IServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IGrainProfiler>(profiler);
        services.AddSingleton<GrainProfilerFilter.GrainMethodFormatterDelegate>(_ =>
        {
            formatterCalls++;
            return "Charge";
        });
        services.Add(filterDescriptor);
        var grain = new TestGrain();
        var invocationCalls = 0;
        var context = CreateProxy<IIncomingGrainCallContext>((method, _) => method.Name switch
        {
            "get_ImplementationMethod" => typeof(TestGrain).GetMethod(nameof(TestGrain.Charge)),
            "get_Grain" => grain,
            nameof(IIncomingGrainCallContext.Invoke) => CountInvocation(),
            _ => throw new NotSupportedException(method.Name),
        });
        Task CountInvocation()
        {
            invocationCalls++;
            return Task.CompletedTask;
        }

        using var provider = services.BuildServiceProvider();
        var filter = provider.GetRequiredService<IIncomingGrainCallFilter>();
        await filter.Invoke(context);

        Assert.IsType<GrainProfilerFilter>(filter);
        Assert.Equal(1, invocationCalls);
        Assert.Equal(1, formatterCalls);
        Assert.Equal(1, profiler.TrackCalls);
        Assert.Equal(typeof(TestGrain), profiler.LastGrainType);
        Assert.Equal("Charge", profiler.LastMethodName);
        Assert.False(profiler.LastFailed);
    }

    [Fact]
    public void AddDashboard_ClientBuilder_RegistersClientGraphWithoutSiloOnlyServices()
    {
        var builder = new TestClientBuilder();

        var returnedBuilder = builder.AddDashboard();

        Assert.Same(builder, returnedBuilder);
        AssertTypeDescriptor<IDashboardClient, DashboardClient>(builder.Services);
        AssertTypeDescriptor<DashboardLogger, DashboardLogger>(builder.Services);
        AssertTypeDescriptor<EmbeddedAssetProvider, EmbeddedAssetProvider>(builder.Services);
        var loggerProvider = Assert.Single(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(ILoggerProvider));
        Assert.Equal(ServiceLifetime.Singleton, loggerProvider.Lifetime);
        Assert.NotNull(loggerProvider.ImplementationFactory);

        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(IGrainService));
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(IGrainProfiler));
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(ISiloDetailsProvider));
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(ISiloGrainClient));
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(DashboardTelemetryExporter));

        using var provider = builder.Services.BuildServiceProvider();
        Assert.Same(
            provider.GetRequiredService<DashboardLogger>(),
            Assert.IsType<DashboardLogger>(provider.GetRequiredService<ILoggerProvider>()));
    }

    [Fact]
    public void AddDashboard_ClientBuilder_CalledTwice_ComposesOptionDelegatesInOrder()
    {
        var builder = new TestClientBuilder();
        builder.AddDashboard(options =>
        {
            options.CounterUpdateIntervalMs = 2_500;
            options.HistoryLength = 12;
        });

        builder.AddDashboard(options =>
        {
            options.HistoryLength = 24;
            options.HideTrace = true;
        });

        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DashboardOptions>>().Value;
        Assert.Equal(2_500, options.CounterUpdateIntervalMs);
        Assert.Equal(24, options.HistoryLength);
        Assert.True(options.HideTrace);
    }

    private static void AssertTypeDescriptor<TService, TImplementation>(IServiceCollection services)
    {
        var descriptor = Assert.Single(
            services,
            candidate => candidate.ServiceType == typeof(TService)
                && candidate.ImplementationType == typeof(TImplementation));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    private static bool IsDashboardRegistration(Type serviceType) =>
        serviceType == typeof(IDashboardClient)
        || serviceType == typeof(ISiloGrainClient)
        || serviceType == typeof(IGrainProfiler)
        || serviceType == typeof(IIncomingGrainCallFilter)
        || serviceType == typeof(EmbeddedAssetProvider)
        || serviceType == typeof(DashboardTelemetryExporter)
        || serviceType == typeof(DashboardLogger)
        || serviceType == typeof(ILoggerProvider)
        || serviceType == typeof(ISiloDetailsProvider)
        || serviceType == typeof(IHostedService)
        || serviceType == typeof(IGrainService);

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler)
        where T : class
    {
        var result = DispatchProxy.Create<T, MethodDispatchProxy>();
        ((MethodDispatchProxy)(object)result).Handler = handler;
        return result;
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class TestClientBuilder : IClientBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class TestProfiler : IGrainProfiler, ILifecycleParticipant<ISiloLifecycle>
    {
        public int TrackCalls { get; private set; }

        public Type? LastGrainType { get; private set; }

        public string? LastMethodName { get; private set; }

        public bool LastFailed { get; private set; }

        public bool IsEnabled => true;

        public void Enable(bool enabled)
        {
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
        }

        public void Track(double elapsedMs, Type grainType, string? methodName = null, bool failed = false)
        {
            TrackCalls++;
            LastGrainType = grainType;
            LastMethodName = methodName;
            LastFailed = failed;
        }
    }

    private sealed class TestGrain
    {
        public void Charge()
        {
        }
    }

    private sealed class TestSiloStatusOracle(Dictionary<SiloAddress, SiloStatus> statuses) : ISiloStatusOracle
    {
        public bool LastOnlyActive { get; private set; }

        public SiloStatus CurrentStatus => SiloStatus.Active;

        public string SiloName => "local";

        public SiloAddress SiloAddress => statuses.Keys.First();

        public SiloAddress[] GetActiveSilos() =>
            [.. statuses.Where(entry => entry.Value == SiloStatus.Active).Select(entry => entry.Key)];

        public SiloStatus GetApproximateSiloStatus(SiloAddress siloAddress) => statuses[siloAddress];

        public Dictionary<SiloAddress, SiloStatus> GetApproximateSiloStatuses(bool onlyActive = false)
        {
            LastOnlyActive = onlyActive;
            return onlyActive
                ? statuses.Where(entry => entry.Value == SiloStatus.Active).ToDictionary()
                : new Dictionary<SiloAddress, SiloStatus>(statuses);
        }

        public bool TryGetSiloName(SiloAddress siloAddress, [NotNullWhen(true)] out string? siloName)
        {
            siloName = siloAddress.ToParsableString();
            return statuses.ContainsKey(siloAddress);
        }

        public bool IsFunctionalDirectory(SiloAddress siloAddress) =>
            GetApproximateSiloStatus(siloAddress) == SiloStatus.Active;

        public bool IsDeadSilo(SiloAddress silo) =>
            GetApproximateSiloStatus(silo) == SiloStatus.Dead;

        public bool SubscribeToSiloStatusEvents(ISiloStatusListener observer) => true;

        public bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener observer) => true;
    }

    private class MethodDispatchProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } =
            static (method, _) => throw new NotSupportedException(method.Name);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod!, args);
    }
}
