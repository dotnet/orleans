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

        public sealed class BaseOnlyService : ServiceBase, IBaseService
        {
            public BaseOnlyService(GrainId id) : base(id)
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

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void SingleInterface_IsUsedForTypeCode()
        {
            Assert.Equal(ExpectedGrainId(typeof(IFlatService)), GetRegisteredGrainId(typeof(FlatService)));
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void InterfaceHierarchy_UsesMostDerivedInterface()
        {
            Assert.Equal(ExpectedGrainId(typeof(ILeafService)), GetRegisteredGrainId(typeof(LeafService)));
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void InterfaceHierarchy_UsesMostDerivedInterface_RegardlessOfDeclarationOrder()
        {
            Assert.Equal(ExpectedGrainId(typeof(ILeafService)), GetRegisteredGrainId(typeof(BaseFirstLeafService)));
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void ThreeLevelHierarchy_UsesMostDerivedInterface_RegardlessOfDeclarationOrder()
        {
            Assert.Equal(ExpectedGrainId(typeof(IDeepLeafService)), GetRegisteredGrainId(typeof(DeepLeafService)));
            Assert.Equal(ExpectedGrainId(typeof(IDeepLeafService)), GetRegisteredGrainId(typeof(BaseFirstDeepLeafService)));
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void UnrelatedInterfaces_DoNotThrow_AndUseOrdinalLowestName()
        {
            Assert.Equal(ExpectedGrainId(typeof(ISiblingServiceA)), GetRegisteredGrainId(typeof(SiblingService)));
            var aliases = GrainServiceInterfaceMap.GetAliasInterfaces(typeof(SiblingService), [typeof(SiblingService)]);
            Assert.Equal([typeof(ISiblingServiceB)], aliases);
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void NoGrainServiceInterface_Throws()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => GetRegisteredGrainId(typeof(NoInterfaceService)));
            Assert.Contains(nameof(NoInterfaceService), exception.Message);
            Assert.Contains(nameof(IGrainService), exception.Message);
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void Hierarchy_AliasesParentInterfacesToTheSameInstance()
        {
            using var directory = CreateDirectory();
            var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
            var primary = GrainIdOn(typeof(IDeepLeafService), silo);
            var service = new AddressableService(primary);
            directory.RecordNewTarget(service);

            var registered = new[] { typeof(DeepLeafService) };
            foreach (var aliasInterface in GrainServiceInterfaceMap.GetAliasInterfaces(typeof(DeepLeafService), registered))
            {
                Assert.True(directory.RecordAlias(GrainServiceInterfaceMap.CreateAliasGrainId(primary, aliasInterface), service));
            }

            Assert.Equal(1, directory.Count);
            Assert.Single(directory);
            Assert.Same(service, directory.FindTarget(primary));
            Assert.Same(service, directory.FindTarget(GrainIdOn(typeof(IMidService), silo)));
            Assert.Same(service, directory.FindTarget(GrainIdOn(typeof(IBaseService), silo)));
            Assert.Equal(primary.Key, GrainServiceInterfaceMap.CreateAliasGrainId(primary, typeof(IBaseService)).Key);
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void SharedBase_IsNotAliasedOntoEitherLeaf()
        {
            var registered = new[] { typeof(LeafService), typeof(OtherLeafService) };
            Assert.Empty(GrainServiceInterfaceMap.GetAliasInterfaces(typeof(LeafService), registered));
            Assert.Empty(GrainServiceInterfaceMap.GetAliasInterfaces(typeof(OtherLeafService), registered));

            var baseAndLeaf = new[] { typeof(BaseOnlyService), typeof(LeafService) };
            Assert.Empty(GrainServiceInterfaceMap.GetAliasInterfaces(typeof(BaseOnlyService), baseAndLeaf));
            Assert.Empty(GrainServiceInterfaceMap.GetAliasInterfaces(typeof(LeafService), baseAndLeaf));
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
