using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.TestingHost;
using Xunit;
using static UnitTests.General.GrainCtorInjectionTests;

namespace UnitTests.General;

public class GrainCtorInjectionTests(TestFixture fixture) : IClassFixture<TestFixture>
{
    [Fact, TestCategory("Functional")]
    public async Task CanActivateGrainWithComplexMixedDISources()
    {
        var grain = fixture.Cluster.Client.GetGrain<IComplexCtorInjectGrain>(Guid.NewGuid());

        var result1 = await grain.GetActivationResult();
        AssertResults(result1);

        await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();

        var result2 = await grain.GetActivationResult();
        AssertResults(result2);

        Assert.NotEqual(result1.ScopedServiceId, result2.ScopedServiceId);
        Assert.NotEqual(result1.TransientServiceId1, result2.TransientServiceId2);
    }

    [Fact, TestCategory("Functional")]
    public async Task CanActivateGrainWithComplexMixedDISources_ReversedOrderInjection()
    {
        var grain = fixture.Cluster.Client.GetGrain<IComplexReversedCtorInjectGrain>(Guid.NewGuid());

        var result1 = await grain.GetActivationResult();
        AssertResults(result1);

        await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();

        var result2 = await grain.GetActivationResult();
        AssertResults(result2);

        Assert.NotEqual(result1.ScopedServiceId, result2.ScopedServiceId);
        Assert.NotEqual(result1.TransientServiceId1, result2.TransientServiceId2);
    }

    private void AssertResults(ActivationResult result)
    {
        Assert.True(result.IsServiceInjected);
        Assert.True(result.IsKeyedServiceInjected);
        Assert.True(result.IsDerivedKeyedServiceInjected);
        Assert.True(result.IsPersistentStateInjected);
        Assert.True(result.IsKeyedPersistentStateInjected);
        Assert.True(result.IsGrainContextInjected);
        Assert.Equal(2, result.EnumerableServiceCount);
        Assert.NotEqual(Guid.Empty, result.ScopedServiceId);
        Assert.NotEqual(Guid.Empty, result.TransientServiceId1);
        Assert.NotEqual(Guid.Empty, result.TransientServiceId2);
    }

    public interface IComplexCtorInjectGrain : IGrainWithGuidKey
    {
        Task<ActivationResult> GetActivationResult();
    }

    public interface IComplexReversedCtorInjectGrain : IGrainWithGuidKey
    {
        Task<ActivationResult> GetActivationResult();
    }

    public class ComplexCtorInjectGrain(
        IEnumerable<IEnumerableService> enumerableServices,
        ScopedService scopedService,
        TransientService transientService1,
        TransientService transientService2,
        Service service,
        [FromKeyedServices("keyed-service")] KeyedService keyedService,
        [FromKeyedServices("keyed-state")] IPersistentState<TestState> keyedPersistentState,
        [FromDerivedKeyedServices("derived-keyed-service")] DerivedKeyedService derivedKeyedService,
        [PersistentState("state", "Default")] IPersistentState<TestState> persistentState,
        IGrainContext grainContext) : Grain, IComplexCtorInjectGrain
    {
        public Task<ActivationResult> GetActivationResult() =>
            Task.FromResult(new ActivationResult
            {
                IsServiceInjected = service is not null,
                IsKeyedServiceInjected = keyedService is not null,
                IsDerivedKeyedServiceInjected = derivedKeyedService is not null,
                IsPersistentStateInjected = persistentState is not null,
                IsKeyedPersistentStateInjected = keyedPersistentState is not null,
                IsGrainContextInjected = grainContext is not null,
                EnumerableServiceCount = enumerableServices.Count(),
                ScopedServiceId = scopedService.ActivationId,
                TransientServiceId1 = transientService1.InstanceId,
                TransientServiceId2 = transientService2.InstanceId
            });
    }

    public class ComplexReversedCtorInjectGrain(
        IGrainContext grainContext,
        [PersistentState("state", "Default")] IPersistentState<TestState> persistentState,
        [FromDerivedKeyedServices("derived-keyed-service")] DerivedKeyedService derivedKeyedService,
        [FromKeyedServices("keyed-state")] IPersistentState<TestState> keyedPersistentState,
        [FromKeyedServices("keyed-service")] KeyedService keyedService,
        Service service,
        TransientService transientService2,
        TransientService transientService1,
        ScopedService scopedService,
        IEnumerable<IEnumerableService> enumerableServices) : Grain, IComplexReversedCtorInjectGrain
    {
        public Task<ActivationResult> GetActivationResult() =>
            Task.FromResult(new ActivationResult
            {
                IsServiceInjected = service is not null,
                IsKeyedServiceInjected = keyedService is not null,
                IsDerivedKeyedServiceInjected = derivedKeyedService is not null,
                IsPersistentStateInjected = persistentState is not null,
                IsKeyedPersistentStateInjected = keyedPersistentState is not null,
                IsGrainContextInjected = grainContext is not null,
                EnumerableServiceCount = enumerableServices.Count(),
                ScopedServiceId = scopedService.ActivationId,
                TransientServiceId1 = transientService1.InstanceId,
                TransientServiceId2 = transientService2.InstanceId
            });
    }

    [GenerateSerializer]
    public class ActivationResult
    {
        [Id(0)] public bool IsServiceInjected { get; set; }
        [Id(1)] public bool IsKeyedServiceInjected { get; set; }
        [Id(2)] public bool IsDerivedKeyedServiceInjected { get; set; }
        [Id(3)] public bool IsPersistentStateInjected { get; set; }
        [Id(4)] public bool IsKeyedPersistentStateInjected { get; set; }
        [Id(5)] public bool IsGrainContextInjected { get; set; }
        [Id(6)] public int EnumerableServiceCount { get; set; }
        [Id(7)] public Guid ScopedServiceId { get; set; }
        [Id(8)] public Guid TransientServiceId1 { get; set; }
        [Id(9)] public Guid TransientServiceId2 { get; set; }
    }

    [GenerateSerializer]
    public class TestState
    {
        [Id(0)] public int Id { get; set; }
    }

    public class Service { }
    public class KeyedService { }
    public class DerivedKeyedService { }
    public class FromDerivedKeyedServices(object key) : FromKeyedServicesAttribute(key);

    public class PersistentState<T> : IPersistentState<T>
    {
        public T State { get; set; }
        public string Etag => default;
        public bool RecordExists => false;
        public Task ClearStateAsync() => Task.CompletedTask;
        public Task ReadStateAsync() => Task.CompletedTask;
        public Task WriteStateAsync() => Task.CompletedTask;
    }

    public interface IEnumerableService
    {
        Guid Id { get; }
    }

    public class EnumerableService1 : IEnumerableService
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    public class EnumerableService2 : IEnumerableService
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    public class ScopedService
    {
        public Guid ActivationId { get; } = Guid.NewGuid();
    }

    public class TransientService
    {
        public Guid InstanceId { get; } = Guid.NewGuid();
    }

    public class TestFixture : IAsyncLifetime
    {
        public readonly InProcessTestCluster Cluster;

        public TestFixture()
        {
            var builder = new InProcessTestClusterBuilder(1);

            builder.ConfigureSilo((options, siloBuilder) =>
            {
                siloBuilder.AddMemoryGrainStorageAsDefault();
                siloBuilder.Services.AddSingleton<Service>();
                siloBuilder.Services.AddKeyedSingleton<KeyedService>("keyed-service");
                siloBuilder.Services.AddKeyedSingleton<DerivedKeyedService>("derived-keyed-service");
                siloBuilder.Services.AddKeyedSingleton<IPersistentState<TestState>, PersistentState<TestState>>("keyed-state");
                siloBuilder.Services.AddSingleton<IEnumerableService, EnumerableService1>();
                siloBuilder.Services.AddSingleton<IEnumerableService, EnumerableService2>();
                siloBuilder.Services.AddScoped<ScopedService>();
                siloBuilder.Services.AddTransient<TransientService>();
            });

            Cluster = builder.Build();
        }

        public virtual async Task InitializeAsync() => await Cluster.DeployAsync();
        public virtual async Task DisposeAsync() => await Cluster.DisposeAsync();
    }
}
