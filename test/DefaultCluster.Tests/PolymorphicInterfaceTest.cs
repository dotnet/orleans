using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Tests for polymorphic grain interfaces in Orleans.
    /// Demonstrates how grains can implement multiple interfaces with inheritance
    /// hierarchies, and how grain references can be cast between compatible interfaces.
    /// This enables object-oriented patterns where grains expose different levels
    /// of functionality through interface inheritance.
    /// </summary>
    public class PolymorphicInterfaceTest : HostedTestClusterEnsureDefaultStarted
    {
        public PolymorphicInterfaceTest(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        /// <summary>
        /// Tests basic polymorphic interface method calls.
        /// Verifies that a grain implementing interface IA can be accessed
        /// through that interface and all methods are properly dispatched.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task Polymorphic_SimpleTest()
        {
            var grainFullName = typeof(PolymorphicTestGrain).FullName;
            IA IARef = this.GrainFactory.GetGrain<IA>(GetRandomGrainId(), grainFullName);
            Assert.Equal("A1", await IARef.A1Method());
            Assert.Equal("A2", await IARef.A2Method());
            Assert.Equal("A3", await IARef.A3Method());
        }

        /// <summary>
        /// Tests upcasting grain references through interface inheritance hierarchies.
        /// Verifies that a grain reference can be cast to any interface in its
        /// inheritance chain and that methods from all interfaces remain accessible
        /// after casting. Demonstrates interface covariance in grain references.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task Polymorphic_UpCastTest()
        {
            var grainFullName = typeof(PolymorphicTestGrain).FullName;
            IC ICRef = this.GrainFactory.GetGrain<IC>(GetRandomGrainId(), grainFullName);
            IA IARef = ICRef; // cast to polymorphic interface
            Assert.Equal("A1", await IARef.A1Method());
            Assert.Equal("A2", await IARef.A2Method());
            Assert.Equal("A3", await IARef.A3Method());

            IB IBRef = ICRef; // cast to polymorphic interface
            Assert.Equal("B1", await IBRef.B1Method());
            Assert.Equal("B2", await IBRef.B2Method());
            Assert.Equal("B3", await IBRef.B3Method());

            IF IFRef = this.GrainFactory.GetGrain<IF>(GetRandomGrainId(), grainFullName);

            Assert.Equal("F1", await IFRef.F1Method());
            Assert.Equal("F2", await IFRef.F2Method());
            Assert.Equal("F3", await IFRef.F3Method());

            IE IERef = IFRef; // cast to polymorphic interface
            Assert.Equal("E1", await IERef.E1Method());
            Assert.Equal("E2", await IERef.E2Method());
            Assert.Equal("E3", await IERef.E3Method());
            
        }

        /// <summary>
        /// Tests factory methods that return polymorphic grain references.
        /// Verifies that grain factory can create grains using one interface type
        /// but return them as a different compatible interface type,
        /// enabling factory patterns with interface abstraction.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task Polymorphic_FactoryMethods()
        {
            var grainFullName = typeof(PolymorphicTestGrain).FullName;
            IC ICRef = this.GrainFactory.GetGrain<IF>(GetRandomGrainId(), grainFullName); // FRef factory method returns a polymorphic reference to ICRef
            Assert.Equal("B2", await ICRef.B2Method());

            IA IARef = this.GrainFactory.GetGrain<ID>(GetRandomGrainId(), grainFullName); // DRef factory method returns a polymorphic reference to IARef
            Assert.Equal("A1", await IARef.A1Method());
        }

        /// <summary>
        /// Tests a service-style grain that implements multiple interfaces.
        /// Verifies that a single grain can expose multiple sets of functionality
        /// through different interfaces, similar to a service exposing multiple contracts.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task Polymorphic_ServiceType()
        {
            var grainFullName = typeof(ServiceType).FullName;
            IServiceType serviceRef = this.GrainFactory.GetGrain<IServiceType>(GetRandomGrainId(), grainFullName);
            Assert.Equal("A1", await serviceRef.A1Method());
            Assert.Equal("A2", await serviceRef.A2Method());
            Assert.Equal("A3", await serviceRef.A3Method());
            Assert.Equal("B1", await serviceRef.B1Method());
            Assert.Equal("B2", await serviceRef.B2Method());
            Assert.Equal("B3", await serviceRef.B3Method());
        }

        /// <summary>
        /// Tests resolution of ambiguous methods in diamond inheritance scenarios.
        /// When multiple interfaces define the same method signature, verifies that
        /// casting to specific interfaces correctly resolves which implementation
        /// is called, following C# interface inheritance rules.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task Polymorphic_InheritedMethodAmbiguity()
        {
            // Tests interface inheritance hierarchies which involve duplicate method names, requiring casting to resolve the ambiguity.
            var grainFullName = typeof(ServiceType).FullName;
            var serviceRef = this.GrainFactory.GetGrain<IServiceType>(GetRandomGrainId(), grainFullName);
            var ia = (IA)serviceRef;
            var ib = (IB)serviceRef;
            var ic = (IC)serviceRef;
            Assert.Equal("IA", await ia.CommonMethod());
            Assert.Equal("IB", await ib.CommonMethod());
            Assert.Equal("IC", await ic.CommonMethod());
        }

        /// <summary>
        /// Comprehensive test for complex polymorphic grain scenarios.
        /// Tests a derived service type that implements multiple interface hierarchies,
        /// verifying that:
        /// - All interfaces in the inheritance chain are accessible
        /// - Cross-casting between unrelated interfaces works when supported
        /// - Derived interface methods are available alongside base methods
        /// This consolidates all polymorphic grain reference use cases.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task Polymorphic__DerivedServiceType()
        {
            var grainFullName = typeof(DerivedServiceType).FullName;
            IDerivedServiceType derivedRef = this.GrainFactory.GetGrain<IDerivedServiceType>(GetRandomGrainId(), grainFullName);

            IA IARef = derivedRef;
            Assert.Equal("A1", await IARef.A1Method());
            Assert.Equal("A2", await IARef.A2Method());
            Assert.Equal("A3", await IARef.A3Method());


            IB IBRef = (IB)IARef; // this could result in an invalid cast exception but it shoudn't because we have a priori knowledge that DerivedServiceType implements the interface
            Assert.Equal("B1", await IBRef.B1Method());
            Assert.Equal("B2", await IBRef.B2Method());
            Assert.Equal("B3", await IBRef.B3Method());

            IF IFRef = (IF)IBRef;
            Assert.Equal("F1", await IFRef.F1Method());
            Assert.Equal("F2", await IFRef.F2Method());
            Assert.Equal("F3", await IFRef.F3Method());

            IE IERef = (IE)IFRef;
            Assert.Equal("E1", await IERef.E1Method());
            Assert.Equal("E2", await IERef.E2Method());
            Assert.Equal("E3", await IERef.E3Method());

            IH IHRef = derivedRef;
            Assert.Equal("H1", await IHRef.H1Method());
            Assert.Equal("H2", await IHRef.H2Method());
            Assert.Equal("H3", await IHRef.H3Method());

            IServiceType serviceTypeRef = derivedRef; // upcast the pointer reference
            Assert.Equal("ServiceTypeMethod1", await serviceTypeRef.ServiceTypeMethod1());
            Assert.Equal("ServiceTypeMethod2", await serviceTypeRef.ServiceTypeMethod2());
            Assert.Equal("ServiceTypeMethod3", await serviceTypeRef.ServiceTypeMethod3());

            Assert.Equal("DerivedServiceTypeMethod1", await derivedRef.DerivedServiceTypeMethod1());
        }
    }
}
