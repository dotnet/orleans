using System.Threading.Tasks;
using NUnit.Framework;
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
    [TestFixture]
    public class InterfaceRulesTests
    {
        #region simple interfaces

        [Test, Category("BVT"), Category("Functional"), Category("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_VoidMethod()
        {
            new GrainInterfaceData(Language.CSharp, typeof(ITestGrain_VoidMethod));
        }

        [Test, Category("BVT"), Category("Functional"), Category("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_IntMethod()
        {
            new GrainInterfaceData(Language.CSharp, typeof(ITestGrain_IntMethod));
        }

        [Test, Category("BVT"), Category("Functional"), Category("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_IntProperty()
        {
            new GrainInterfaceData(Language.CSharp, typeof(ITestGrain_IntProperty));
        }

        [Test, Category("BVT"), Category("Functional"), Category("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_PropertySetter()
        {
            new GrainInterfaceData(Language.CSharp, typeof(ITestGrain_PropertySetter));
        }

        [Test, Category("BVT"), Category("Functional"), Category("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_Observer_NonVoidMethod()
        {
            new GrainInterfaceData(Language.CSharp, typeof(ITestObserver_NonVoidMethod));
        }

        [Test, Category("BVT"), Category("Functional"), Category("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_Observer_Property()
        {
            new GrainInterfaceData(Language.CSharp, typeof(ITestObserver_Property));
        }

        [Test, Category("BVT"), Category("Functional"), Category("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_OutArgument()
        {
            new GrainInterfaceData(Language.CSharp, typeof(ITestGrain_OutArgument));
        }

        [Test, Category("BVT"), Category("Functional"), Category("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_RefArgument()
        {
            new GrainInterfaceData(Language.CSharp, typeof(ITestGrain_RefArgument));
        }

        #endregion

        #region inheritence

        [Test, Category("BVT"), Category("Functional"), Category("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_ObserverGrain_VoidMethod()
        {
            new GrainInterfaceData(Language.CSharp, typeof(IInheritedGrain_ObserverGrain_VoidMethod));
        }

        [Test, Category("BVT"), Category("Functional"), Category("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_ObserverGrain_IntMethod()
        {
            new GrainInterfaceData(Language.CSharp, typeof(IInheritedGrain_ObserverGrain_IntMethod));
        }

        [Test, Category("BVT"), Category("Functional"), Category("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_ObserverGrain_IntProperty()
        {
            new GrainInterfaceData(Language.CSharp, typeof(IInheritedGrain_ObserverGrain_IntProperty));
        }

        [Test, Category("BVT"), Category("Functional"), Category("CodeGen")]
        [ExpectedException(typeof(GrainInterfaceData.RulesViolationException))]
        public void InterfaceRules_ObserverGrain_PropertySetter()
        {
            new GrainInterfaceData(Language.CSharp, typeof(IInheritedGrain_ObserverGrain_PropertySetter));
        }

        #endregion
    }
}
