using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Services;
using TestExtensions;
using Xunit;

namespace Tester
{
    /// <summary>
    /// Tests which verify that a grain service is registered under the type code of the interface which its clients use,
    /// regardless of how many interfaces in its hierarchy extend <see cref="IGrainService"/>.
    /// </summary>
    public class GrainServiceInterfaceResolutionTests
    {
        public interface IFlatService : IGrainService
        {
        }

        public interface IBaseService : IGrainService
        {
        }

        public interface ILeafService : IBaseService
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

        public sealed class SiblingService : ServiceBase, ISiblingServiceA, ISiblingServiceB
        {
            public SiblingService(GrainId id) : base(id)
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
        public void UnrelatedInterfaces_Throw()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => GetRegisteredGrainId(typeof(SiblingService)));
            Assert.Contains(nameof(ISiblingServiceA), exception.Message);
            Assert.Contains(nameof(ISiblingServiceB), exception.Message);
        }

        [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
        public void NoGrainServiceInterface_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => GetRegisteredGrainId(typeof(NoInterfaceService)));
        }

        private static GrainId GetRegisteredGrainId(Type serviceType)
        {
            var services = new ServiceCollection();
            services.AddGrainService(serviceType);
            using var provider = services.BuildServiceProvider();
            return ((ServiceBase)provider.GetRequiredService<IGrainService>()).Id;
        }

        private static GrainId ExpectedGrainId(Type interfaceType)
        {
            var typeCode = GrainInterfaceUtils.GetGrainClassTypeCode(interfaceType);
            return SystemTargetGrainId.CreateGrainServiceGrainId(typeCode, null!, SiloAddress.Zero);
        }
    }
}
