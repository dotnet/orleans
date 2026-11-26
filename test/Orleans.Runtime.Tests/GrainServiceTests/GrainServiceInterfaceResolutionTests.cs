using System.Diagnostics.Metrics;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Orleans.Services;
using TestExtensions;
using Xunit;

namespace Tester
{
    /// <summary>
    /// Tests which verify the grain-service system-target identity selected at registration.
    /// </summary>
    public class GrainServiceInterfaceResolutionTests
    {
        public interface IFlatService : IGrainService
        {
        }

        public interface IBaseService : IGrainService
        {
        }

        public interface IMidService : IBaseService
        {
        }

        public interface ILeafService : IBaseService
        {
        }

        public interface IDeepLeafService : IMidService
        {
        }

        public interface ISiblingServiceA : IGrainService
        {
        }

        public interface ISiblingServiceB : IGrainService
        {
        }

        public abstract class ServiceBase
        {
            protected ServiceBase(GrainId id)
            {
                Id = id;
            }

            public GrainId Id { get; }
        }

        public sealed class NoInterfaceService : ServiceBase
        {
            public NoInterfaceService(GrainId id) : base(id)
            {
            }
        }

        public sealed class FlatService : ServiceBase, IFlatService
        {
            public FlatService(GrainId id) : base(id)
            {
            }
        }

        public sealed class LeafService : ServiceBase, ILeafService
        {
            public LeafService(GrainId id) : base(id)
            {
            }
        }

        // Declares the parent interface before the leaf, so the order of Type.GetInterfaces() differs from LeafService.
        public sealed class BaseFirstLeafService : ServiceBase, IBaseService, ILeafService
        {
            public BaseFirstLeafService(GrainId id) : base(id)
            {
            }
        }

        public sealed class DeepLeafService : ServiceBase, IDeepLeafService
        {
            public DeepLeafService(GrainId id) : base(id)
            {
            }
        }

        public sealed class BaseFirstDeepLeafService : ServiceBase, IBaseService, IMidService, IDeepLeafService
        {
            public BaseFirstDeepLeafService(GrainId id) : base(id)
            {
            }
        }

        public sealed class SiblingService : ServiceBase, ISiblingServiceA, ISiblingServiceB
        {
            public SiblingService(GrainId id) : base(id)
            {
            }
        }

        public interface IOtherLeafService : IBaseService
        {
        }

        public sealed class OtherLeafService : ServiceBase, IOtherLeafService
        {
            public OtherLeafService(GrainId id) : base(id)
            {
            }
        }

        // Lists the ordinally later name first, so a FullName sort would pick the other interface.
        public sealed class SiblingBFirstService : ServiceBase, ISiblingServiceB, ISiblingServiceA
        {
            public SiblingBFirstService(GrainId id) : base(id)
            {
            }
        }

        public sealed class FlatAndSiblingService : ServiceBase, IFlatService, ISiblingServiceA
        {
            public FlatAndSiblingService(GrainId id) : base(id)
            {
            }
        }

        public sealed class FlatAndOtherSiblingService : ServiceBase, IFlatService, ISiblingServiceB
        {
            public FlatAndOtherSiblingService(GrainId id) : base(id)
            {
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void SingleInterface_IsUsedForTypeCode()
        {
            AssertPrimary(typeof(FlatService), typeof(IFlatService));
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void LeafOnly_KeepsLeafAndAliasesBase()
        {
            AssertPrimary(typeof(LeafService), typeof(ILeafService));
            AssertSameInstance(typeof(LeafService), typeof(ILeafService), typeof(IBaseService));
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void BaseDeclaredFirst_KeepsBaseAndAliasesLeaf()
        {
            AssertPrimary(typeof(BaseFirstLeafService), typeof(IBaseService));
            AssertSameInstance(typeof(BaseFirstLeafService), typeof(IBaseService), typeof(ILeafService));
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void ThreeLevelHierarchy_KeepsTheLegacyPrimary()
        {
            AssertPrimary(typeof(DeepLeafService), typeof(IDeepLeafService));
            AssertSameInstance(typeof(DeepLeafService), typeof(IDeepLeafService), typeof(IMidService), typeof(IBaseService));

            AssertPrimary(typeof(BaseFirstDeepLeafService), typeof(IBaseService));
            AssertSameInstance(typeof(BaseFirstDeepLeafService), typeof(IBaseService), typeof(IMidService), typeof(IDeepLeafService));
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void UnrelatedInterfaces_DoNotThrow_AndKeepTheFirstInterface()
        {
            AssertPrimary(typeof(SiblingService), typeof(ISiblingServiceA));
            Assert.Equal([typeof(ISiblingServiceB)], GrainServiceInterfaceMap.GetAliasInterfaces(typeof(SiblingService), [typeof(SiblingService)]));

            AssertPrimary(typeof(SiblingBFirstService), typeof(ISiblingServiceB));
            Assert.Equal([typeof(ISiblingServiceA)], GrainServiceInterfaceMap.GetAliasInterfaces(typeof(SiblingBFirstService), [typeof(SiblingBFirstService)]));
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void NoGrainServiceInterface_Throws()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => GetRegisteredGrainId(typeof(NoInterfaceService)));
            Assert.Contains(nameof(NoInterfaceService), exception.Message);
            Assert.Contains(nameof(IGrainService), exception.Message);
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void BaseFirstAndLeaf_KeepDistinctLegacyOwners()
        {
            var registered = new[] { typeof(BaseFirstLeafService), typeof(LeafService) };
            AssertPrimary(typeof(BaseFirstLeafService), typeof(IBaseService));
            AssertPrimary(typeof(LeafService), typeof(ILeafService));

            using var directory = CreateDirectory();
            var silo = NewSilo();
            var baseService = Register(directory, typeof(IBaseService), silo);
            var leafService = Register(directory, typeof(ILeafService), silo);
            PublishAliases(directory, baseService, typeof(BaseFirstLeafService), registered);
            PublishAliases(directory, leafService, typeof(LeafService), registered);

            Assert.Same(baseService, directory.FindTarget(baseService.GrainId));
            Assert.Same(leafService, directory.FindTarget(leafService.GrainId));
            Assert.NotSame(baseService, leafService);
            Assert.Equal(2, directory.Count);
            Assert.Empty(GrainServiceInterfaceMap.GetAliasInterfaces(typeof(BaseFirstLeafService), registered));
            Assert.Empty(GrainServiceInterfaceMap.GetAliasInterfaces(typeof(LeafService), registered));
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void BaseFirstAndOtherLeaf_KeepTheSharedBaseOnItsLegacyOwner()
        {
            var registered = new[] { typeof(BaseFirstLeafService), typeof(OtherLeafService) };
            AssertPrimary(typeof(BaseFirstLeafService), typeof(IBaseService));
            AssertPrimary(typeof(OtherLeafService), typeof(IOtherLeafService));

            using var directory = CreateDirectory();
            var silo = NewSilo();
            var baseService = Register(directory, typeof(IBaseService), silo);
            var otherService = Register(directory, typeof(IOtherLeafService), silo);
            PublishAliases(directory, baseService, typeof(BaseFirstLeafService), registered);
            PublishAliases(directory, otherService, typeof(OtherLeafService), registered);

            Assert.Same(baseService, directory.FindTarget(GrainIdOn(typeof(IBaseService), silo)));
            Assert.Same(otherService, directory.FindTarget(GrainIdOn(typeof(IOtherLeafService), silo)));
            Assert.Same(baseService, directory.FindTarget(GrainIdOn(typeof(ILeafService), silo)));
            Assert.NotSame(otherService, directory.FindTarget(GrainIdOn(typeof(IBaseService), silo)));
            Assert.Equal(2, directory.Count);
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void UnrelatedLeaves_DoNotReceiveTheSharedBase()
        {
            var registered = new[] { typeof(LeafService), typeof(OtherLeafService) };
            AssertPrimary(typeof(LeafService), typeof(ILeafService));
            AssertPrimary(typeof(OtherLeafService), typeof(IOtherLeafService));
            Assert.Empty(GrainServiceInterfaceMap.GetAliasInterfaces(typeof(LeafService), registered));
            Assert.Empty(GrainServiceInterfaceMap.GetAliasInterfaces(typeof(OtherLeafService), registered));

            using var directory = CreateDirectory();
            var silo = NewSilo();
            var leafService = Register(directory, typeof(ILeafService), silo);
            var otherService = Register(directory, typeof(IOtherLeafService), silo);
            PublishAliases(directory, leafService, typeof(LeafService), registered);
            PublishAliases(directory, otherService, typeof(OtherLeafService), registered);

            Assert.Same(leafService, directory.FindTarget(leafService.GrainId));
            Assert.Same(otherService, directory.FindTarget(otherService.GrainId));
            Assert.Null(directory.FindTarget(GrainIdOn(typeof(IBaseService), silo)));
            Assert.Equal(2, directory.Count);
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void SameLegacyPrimary_DoesNotAliasTheLoserOntoTheWinner()
        {
            var registered = new[] { typeof(FlatAndSiblingService), typeof(FlatAndOtherSiblingService) };
            AssertPrimary(typeof(FlatAndSiblingService), typeof(IFlatService));
            AssertPrimary(typeof(FlatAndOtherSiblingService), typeof(IFlatService));

            using var directory = CreateDirectory();
            var silo = NewSilo();
            var winner = Register(directory, typeof(IFlatService), silo);
            var loser = new AddressableService(winner.GrainId);
            directory.RecordNewTarget(loser);
            PublishAliases(directory, winner, typeof(FlatAndSiblingService), registered);
            PublishAliases(directory, loser, typeof(FlatAndOtherSiblingService), registered);

            Assert.Same(winner, directory.FindTarget(winner.GrainId));
            Assert.Same(winner, directory.FindTarget(GrainIdOn(typeof(ISiblingServiceA), silo)));
            Assert.Null(directory.FindTarget(GrainIdOn(typeof(ISiblingServiceB), silo)));
            Assert.Equal(1, directory.Count);
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void SharedBase_ExistingPrimaryIsLeftInPlace()
        {
            using var directory = CreateDirectory();
            var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
            var baseService = new AddressableService(GrainIdOn(typeof(IBaseService), silo));
            var leafService = new AddressableService(GrainIdOn(typeof(ILeafService), silo));
            directory.RecordNewTarget(baseService);
            directory.RecordNewTarget(leafService);

            Assert.False(directory.RecordAlias(baseService.GrainId, leafService));
            Assert.Same(baseService, directory.FindTarget(baseService.GrainId));
            Assert.Same(leafService, directory.FindTarget(leafService.GrainId));
            Assert.Equal(2, directory.Count);
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void Alias_IsRemovedAndDisposedWithThePrimary()
        {
            using var directory = CreateDirectory();
            var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
            var primary = GrainIdOn(typeof(ILeafService), silo);
            var alias = GrainIdOn(typeof(IBaseService), silo);
            var service = new AddressableService(primary);
            directory.RecordNewTarget(service);
            Assert.True(directory.RecordAlias(alias, service));
            Assert.Same(service, directory.FindTarget(alias));

            Assert.True(directory.RemoveTarget(service));
            Assert.Null(directory.FindTarget(primary));
            Assert.Null(directory.FindTarget(alias));
            Assert.Equal(0, directory.Count);

            directory.RecordNewTarget(service);
            Assert.True(directory.RecordAlias(alias, service));
            ((IDisposable)directory).Dispose();
            Assert.Equal(1, service.DisposeCount);
            Assert.Null(directory.FindTarget(alias));
            ((IDisposable)directory).Dispose();
            Assert.Equal(1, service.DisposeCount);
        }

        private static void AssertPrimary(Type serviceType, Type interfaceType)
        {
            var legacy = Array.Find(serviceType.GetInterfaces(), static candidate => candidate.GetInterfaces().Contains(typeof(IGrainService)));
            Assert.Equal(interfaceType, legacy);
            Assert.Equal(interfaceType, GrainServiceInterfaceMap.GetPrimaryInterface(serviceType));
            Assert.Equal(ExpectedGrainId(interfaceType), GetRegisteredGrainId(serviceType));
        }

        private static void AssertSameInstance(Type serviceType, params Type[] interfaceTypes)
        {
            using var directory = CreateDirectory();
            var silo = NewSilo();
            var registered = new[] { serviceType };
            var primaryInterface = GrainServiceInterfaceMap.GetPrimaryInterface(serviceType);
            var service = Register(directory, primaryInterface, silo);
            PublishAliases(directory, service, serviceType, registered);

            Assert.Equal(1, directory.Count);
            Assert.Single(directory);
            foreach (var interfaceType in interfaceTypes)
            {
                Assert.Same(service, directory.FindTarget(GrainIdOn(interfaceType, silo)));
            }

            Assert.Equal(service.GrainId.Key, GrainServiceInterfaceMap.CreateAliasGrainId(service.GrainId, interfaceTypes[0]).Key);
        }

        private static AddressableService Register(ActivationDirectory directory, Type interfaceType, SiloAddress silo)
        {
            var service = new AddressableService(GrainIdOn(interfaceType, silo));
            directory.RecordNewTarget(service);
            return service;
        }

        private static void PublishAliases(ActivationDirectory directory, AddressableService service, Type serviceType, IReadOnlyList<Type> registered)
        {
            if (!ReferenceEquals(directory.FindTarget(service.GrainId), service))
            {
                return;
            }

            foreach (var aliasInterface in GrainServiceInterfaceMap.GetAliasInterfaces(serviceType, registered))
            {
                Assert.True(directory.RecordAlias(GrainServiceInterfaceMap.CreateAliasGrainId(service.GrainId, aliasInterface), service));
            }
        }

        private static SiloAddress NewSilo() => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);

        private static GrainId GetRegisteredGrainId(Type serviceType)
        {
            var services = new ServiceCollection();
            services.AddGrainService(serviceType);
            using var provider = services.BuildServiceProvider();
            return ((ServiceBase)provider.GetRequiredService<IGrainService>()).Id;
        }

        private static GrainId ExpectedGrainId(Type interfaceType) => GrainIdOn(interfaceType, SiloAddress.Zero);

        private static GrainId GrainIdOn(Type interfaceType, SiloAddress silo)
        {
            var typeCode = GrainInterfaceUtils.GetGrainClassTypeCode(interfaceType);
            return SystemTargetGrainId.CreateGrainServiceGrainId(typeCode, null!, silo);
        }

        private static ActivationDirectory CreateDirectory()
        {
            var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            return new ActivationDirectory(new CatalogInstruments(new OrleansInstruments(provider.GetRequiredService<IMeterFactory>())));
        }

        private sealed class AddressableService : IGrainContext, IDisposable
        {
            public AddressableService(GrainId id) => GrainId = id;

            public GrainId GrainId { get; }

            public int DisposeCount { get; private set; }

            public GrainReference GrainReference => throw new NotSupportedException();

            public object? GrainInstance => null;

            public ActivationId ActivationId => throw new NotSupportedException();

            public GrainAddress Address => throw new NotSupportedException();

            public IServiceProvider ActivationServices => throw new NotSupportedException();

            public IGrainLifecycle ObservableLifecycle => throw new NotSupportedException();

            public IWorkItemScheduler Scheduler => throw new NotSupportedException();

            public Task Deactivated => Task.CompletedTask;

            public void Dispose() => DisposeCount++;

            public void SetComponent<TComponent>(TComponent? value) where TComponent : class => throw new NotSupportedException();

            public void ReceiveMessage(object message) => throw new NotSupportedException();

            public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public void Rehydrate(IRehydrationContext context) => throw new NotSupportedException();

            public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public object? GetTarget() => this;

            public object? GetComponent(Type componentType) => null;

            public bool Equals(IGrainContext? other) => ReferenceEquals(this, other);

            public override bool Equals(object? obj) => obj is IGrainContext other && Equals(other);

            public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
        }
    }
}
