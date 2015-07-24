using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using GrainInterfaceData = Orleans.CodeGeneration.GrainInterfaceData;

namespace UnitTests.General
{
    #region simple interfaces

    public interface ITestGrain_VoidMethod : IAddressable
    {
        void VoidMethod();
    }

    public interface ITestGrain_IntMethod : IAddressable
    {
        int IntMethod();
    }

    public interface ITestGrain_IntProperty : IAddressable
    {
        int IntProperty { get; }
    }

    public interface ITestGrain_PropertySetter : IAddressable
    {
        Task<int> IntProperty { get; set; }
    }

    public interface ITestObserver_NonVoidMethod : IGrainObserver
    {
        Task NonVoidMethod();
    }

    public interface ITestObserver_Property : IGrainObserver
    {
        Task<int> IntProperty { get; }
    }

    public interface ITestGrain_OutArgument : IAddressable
    {
        Task Method(out int parameter);
    }

    public interface ITestGrain_RefArgument : IAddressable
    {
        Task Method(ref int parameter);
    }

    #endregion

    #region inheritance

    public interface IBaseGrain : IAddressable
    {
    }

    public interface IBaseObserver : IGrainObserver
    {
    }

    public interface IInheritedGrain_ObserverGrain_VoidMethod : IBaseGrain, IBaseObserver
    {
        void VoidMethod();
    }

    public interface IInheritedGrain_ObserverGrain_IntMethod : IBaseGrain, IBaseObserver
    {
        int IntMethod();
    }

    public interface IInheritedGrain_ObserverGrain_IntProperty : IBaseGrain, IBaseObserver
    {
        int IntProperty { get; }
    }
    
    public interface IInheritedGrain_ObserverGrain_PropertySetter : IBaseGrain, IBaseObserver
    {
        Task<int> IntProperty { get; set; }
    }

    public interface IBaseTaskGrain : IAddressable
    {
        Task VoidMethod();
    }

    public interface IBasePromiseGrain : IAddressable
    {
        Task VoidMethod();
    }

    public interface IDerivedTaskGrain : IBaseTaskGrain
    {
    }

    public interface IDerivedPromiseGrain : IBasePromiseGrain
    {
    }

    public interface IDerivedTaskGrainWithGrainRef : IBaseTaskGrain
    {
        Task<IBasePromiseGrain> GetGrain();
    }

    public interface IDerivedPromiseGrainWithGrainRef : IBasePromiseGrain
    {
        Task<IBaseTaskGrain> GetGrain();
    }

    #endregion

    /// <summary>
    /// Summary description for InterfaceRules
    /// </summary>
    [TestClass]
    public class InterfaceRulesTests
    {
        [TestInitialize]
        [TestCleanup]
        public void MyTestCleanup()
        {
        }

        public TestContext TestContext { get; set; }

        #region simple interfaces

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_VoidMethod()
        {
            new GrainInterfaceData(Language.CSharp, typeof(ITestGrain_VoidMethod));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_IntMethod()
        {
            new GrainInterfaceData(Language.CSharp, typeof(ITestGrain_IntMethod));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_IntProperty()
        {
            new GrainInterfaceData(Language.CSharp, typeof(ITestGrain_IntProperty));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_PropertySetter()
        {
            new GrainInterfaceData(Language.CSharp, typeof(ITestGrain_PropertySetter));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_Observer_NonVoidMethod()
        {
            new GrainInterfaceData(Language.CSharp, typeof(ITestObserver_NonVoidMethod));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_Observer_Property()
        {
            new GrainInterfaceData(Language.CSharp, typeof(ITestObserver_Property));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_OutArgument()
        {
            new GrainInterfaceData(Language.CSharp, typeof(ITestGrain_OutArgument));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_RefArgument()
        {
            new GrainInterfaceData(Language.CSharp, typeof(ITestGrain_RefArgument));
        }

        #endregion

        #region inheritence

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_ObserverGrain_VoidMethod()
        {
            new GrainInterfaceData(Language.CSharp, typeof(IInheritedGrain_ObserverGrain_VoidMethod));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_ObserverGrain_IntMethod()
        {
            new GrainInterfaceData(Language.CSharp, typeof(IInheritedGrain_ObserverGrain_IntMethod));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_ObserverGrain_IntProperty()
        {
            new GrainInterfaceData(Language.CSharp, typeof(IInheritedGrain_ObserverGrain_IntProperty));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_ObserverGrain_PropertySetter()
        {
            new GrainInterfaceData(Language.CSharp, typeof(IInheritedGrain_ObserverGrain_PropertySetter));
        }

        #endregion
    }
}
