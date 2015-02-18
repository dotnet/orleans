using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using GeneratorTestGrain;
using Orleans.CodeGeneration;
using MultifacetGrain;
using Orleans;
using Orleans.Runtime;
using GrainInterfaceData = Orleans.CodeGeneration.GrainInterfaceData;
using UnitTests.General;
using UnitTests.GrainInterfaces;


namespace UnitTests
{
    [TestClass]
    public class GrainReferenceCastTests : UnitTestBase
    {
        public GrainReferenceCastTests()
            : base(false)
        {
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void CastCheckReferenceNameForGrainClass()
        {
            GrainInterfaceData si = new GrainInterfaceData(typeof(ISimpleGrain));
            Assert.AreEqual("SimpleGrainReference", si.ReferenceClassName);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void CastCheckReferenceNameForGrainInterface()
        {
            GrainInterfaceData si = new GrainInterfaceData(typeof(IMultifacetTestGrain));
            Assert.AreEqual("MultifacetTestGrainReference", si.ReferenceClassName);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        [ExpectedException(typeof(ArgumentNullException))]
        public void CastExceptionContentsForNull()
        {
            GrainReference cast = (GrainReference)SimpleGrainFactory.Cast(null);
            Assert.Fail("Exception should have been raised");
        }

        // TODO: [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        [TestMethod, TestCategory("Cast")]
        public void CastExceptionContentsForCannotCast()
        {
            try
            {
                GrainReference grain = (GrainReference) GeneratorTestGrainFactory.GetGrain(GetRandomGrainId());
                ISimpleGrain cast = SimpleGrainFactory.Cast(grain);
                Assert.Fail("Exception should have been raised");
            }
            catch (InvalidCastException ice)
            {
                Assert.IsNotNull(ice.Message);
                Assert.IsTrue(ice.Message.Contains("GeneratorTestGrain"));
            }
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void CastGrainRefCastFromMyType()
        {
            GrainReference grain = (GrainReference)TestConstants.GetSimpleGrain();
            GrainReference cast = (GrainReference)SimpleGrainFactory.Cast(grain);
            Assert.IsInstanceOfType(cast, grain.GetType());
            Assert.IsInstanceOfType(cast, typeof(ISimpleGrain));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void CastGrainRefCastFromMyTypePolymorphic()
        {
            // MultifacetTestGrain implements IMultifacetReader
            // MultifacetTestGrain implements IMultifacetWriter
            IAddressable grain = MultifacetTestGrainFactory.GetGrain(0);
            Assert.IsInstanceOfType(grain, typeof(IMultifacetWriter));
            Assert.IsInstanceOfType(grain, typeof(IMultifacetReader));

            IAddressable cast = MultifacetWriterFactory.Cast(grain);
            Assert.IsInstanceOfType(cast, grain.GetType());
            Assert.IsInstanceOfType(cast, typeof(IMultifacetWriter));
            Assert.IsInstanceOfType(grain, typeof(IMultifacetReader));

            IAddressable cast2 = MultifacetReaderFactory.Cast(grain);
            Assert.IsInstanceOfType(cast2, grain.GetType());
            Assert.IsInstanceOfType(cast2, typeof(IMultifacetReader));
            Assert.IsInstanceOfType(grain, typeof(IMultifacetWriter));
        }

        // Test case currently fails intermittently
        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void CastMultifacetRWReference()
        {
            // MultifacetTestGrain implements IMultifacetReader
            // MultifacetTestGrain implements IMultifacetWriter
            int newValue = 3;

            IMultifacetWriter writer = MultifacetWriterFactory.GetGrain(1);
            // No Wait in this test case

            IMultifacetReader reader = MultifacetReaderFactory.Cast(writer);  // --> Test case intermittently fails here
            // Error: System.InvalidCastException: Grain reference MultifacetGrain.MultifacetWriterFactory+MultifacetWriterReference service interface mismatch: expected interface id=[1947430462] received interface name=[MultifacetGrain.IMultifacetWriter] id=[62435819] in grainRef=[GrainReference:*std/b198f19f]

            writer.SetValue(newValue).Wait();

            Task<int> readAsync = reader.GetValue();
            readAsync.Wait();
            int result = readAsync.Result;

            Assert.AreEqual(newValue, result);
        }

        // Test case currently fails
        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void CastMultifacetRWReferenceWaitForResolve()
        {
            // MultifacetTestGrain implements IMultifacetReader
            // MultifacetTestGrain implements IMultifacetWriter

            //Interface Id values for debug:
            // IMultifacetWriter = 62435819
            // IMultifacetReader = 1947430462
            // IMultifacetTestGrain = 222717230 (also compatable with 1947430462 or 62435819)

            int newValue = 4;

            IMultifacetWriter writer = MultifacetWriterFactory.GetGrain(2);
            
            IMultifacetReader reader = MultifacetReaderFactory.Cast(writer); // --> Test case fails here
            // Error: System.InvalidCastException: Grain reference MultifacetGrain.MultifacetWriterFactory+MultifacetWriterReference service interface mismatch: expected interface id=[1947430462] received interface name=[MultifacetGrain.IMultifacetWriter] id=[62435819] in grainRef=[GrainReference:*std/8408c2bc]
            
            writer.SetValue(newValue).Wait();

            int result = reader.GetValue().Result;

            Assert.AreEqual(newValue, result);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void ConfirmServiceInterfacesListContents()
        {
            // GeneratorTestDerivedDerivedGrainReference extends GeneratorTestDerivedGrain2Reference
            // GeneratorTestDerivedGrain2Reference extends GeneratorTestGrainReference
            Type t1 = typeof(IGeneratorTestDerivedDerivedGrain);
            Type t2 = typeof(IGeneratorTestDerivedGrain2);
            Type t3 = typeof(IGeneratorTestGrain);
            int id1 = GrainInterfaceData.GetGrainInterfaceId(t1);
            int id2 = GrainInterfaceData.GetGrainInterfaceId(t2);
            int id3 = GrainInterfaceData.GetGrainInterfaceId(t3); 

            var interfaces = GrainInterfaceData.GetRemoteInterfaces(typeof(IGeneratorTestDerivedDerivedGrain));
            Assert.IsNotNull(interfaces);
            Assert.AreEqual(3, interfaces.Keys.Count);
            Assert.IsTrue(interfaces.Keys.Contains(id1), "id1 is present");
            Assert.IsTrue(interfaces.Keys.Contains(id2), "id2 is present");
            Assert.IsTrue(interfaces.Keys.Contains(id3), "id3 is present");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void CastCheckExpectedCompatIds()
        {
            Type t = typeof(ISimpleGrain);
            int expectedInterfaceId = GrainInterfaceData.GetGrainInterfaceId(t);
            GrainReference grain = (GrainReference)TestConstants.GetSimpleGrain();
            Assert.IsTrue(grain.IsCompatible(expectedInterfaceId));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void CastCheckExpectedCompatIds2()
        {
            // GeneratorTestDerivedDerivedGrainReference extends GeneratorTestDerivedGrain2Reference
            // GeneratorTestDerivedGrain2Reference extends GeneratorTestGrainReference
            Type t1 = typeof(IGeneratorTestDerivedDerivedGrain);
            Type t2 = typeof(IGeneratorTestDerivedGrain2);
            Type t3 = typeof(IGeneratorTestGrain);
            int id1 = GrainInterfaceData.GetGrainInterfaceId(t1);
            int id2 = GrainInterfaceData.GetGrainInterfaceId(t2);
            int id3 = GrainInterfaceData.GetGrainInterfaceId(t3);
            GrainReference grain = (GrainReference) GeneratorTestDerivedDerivedGrainFactory.GetGrain(GetRandomGrainId());
            Assert.IsTrue(grain.IsCompatible(id1));
            Assert.IsTrue(grain.IsCompatible(id2));
            Assert.IsTrue(grain.IsCompatible(id3));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        [ExpectedExceptionAttribute(typeof(InvalidCastException))]
        public void CastFailInternalCastFromBadType()
        {
            Type t = typeof(ISimpleGrain);
            GrainReference grain = (GrainReference)TestConstants.GetSimpleGrain();
            IAddressable cast = GrainReference.CastInternal(
                typeof(Boolean),
                null,
                grain,
                GrainInterfaceData.GetGrainInterfaceId(t));
            Assert.Fail("Exception should have been raised");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void CastInternalCastFromMyType()
        {
            const string serviceName = UnitTestGrainInterfacesConstants.SimpleGrainFullClassName;
            GrainReference grain = (GrainReference)TestConstants.GetSimpleGrain();
            
            IAddressable cast = GrainReference.CastInternal(
                typeof(ISimpleGrain),
                (GrainReference gr) => { throw new InvalidOperationException("Should not need to create a new GrainReference wrapper"); },
                grain,
                Utils.CalculateIdHash(serviceName));

            Assert.IsInstanceOfType(cast, typeof(ISimpleGrain));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void CastInternalCastUpFromChild()
        {
            // GeneratorTestDerivedGrain1Reference extends GeneratorTestGrainReference
            GrainReference grain = (GrainReference) GeneratorTestDerivedGrain1Factory.GetGrain(GetRandomGrainId());
            
            const string serviceName = "GeneratorTestGrain.GeneratorTestGrain";
            IAddressable cast = GrainReference.CastInternal(
                typeof(IGeneratorTestGrain),
                (GrainReference gr) => { throw new InvalidOperationException("Should not need to create a new GrainReference wrapper"); },
                grain,
                Utils.CalculateIdHash(serviceName));

            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestGrain));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void CastGrainRefUpCastFromChild()
        {
            // GeneratorTestDerivedGrain1Reference extends GeneratorTestGrainReference
            GrainReference grain = (GrainReference) GeneratorTestDerivedGrain1Factory.GetGrain(GetRandomGrainId());
            GrainReference cast = (GrainReference) GeneratorTestGrainFactory.Cast(grain);
            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestDerivedGrain1));
            Assert.IsInstanceOfType(cast,typeof(IGeneratorTestGrain));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        [ExpectedExceptionAttribute(typeof(InvalidCastException))]
        public void FailSideCastAfterResolve()
        {
            // GeneratorTestDerivedGrain1Reference extends GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference extends GeneratorTestGrainReference
            try
            {
                IGeneratorTestDerivedGrain1 grain = GeneratorTestDerivedGrain1Factory.GetGrain(GetRandomGrainId());
                Assert.IsTrue(grain.StringIsNullOrEmpty().Result);
                // Fails the next line as grain reference is already resolved
                IGeneratorTestDerivedGrain2 cast = GeneratorTestDerivedGrain2Factory.Cast(grain);
                Task<string> av = cast.StringConcat("a", "b", "c");
                av.Wait();
                Assert.IsFalse(cast.StringIsNullOrEmpty().Result); // Not reached
            }
            catch (AggregateException ae)
            {
                Exception ex = ae.InnerException;
                while (ex is AggregateException) ex = ex.InnerException;
                throw ex;
            }
            Assert.Fail("Exception should have been raised");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        [ExpectedExceptionAttribute(typeof(InvalidCastException))]
        public void FailOperationAfterSideCast()
        {
            // GeneratorTestDerivedGrain1Reference extends GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference extends GeneratorTestGrainReference
            try
            {
                IGeneratorTestDerivedGrain1 grain = GeneratorTestDerivedGrain1Factory.GetGrain(GetRandomGrainId());
                // Cast works optimistically when the grain reference is not already resolved
                IGeneratorTestDerivedGrain2 cast = GeneratorTestDerivedGrain2Factory.Cast(grain);
                // Operation fails when grain reference is completely resolved
                Task<string> av = cast.StringConcat("a", "b", "c");
                string val = av.Result;
            }
            catch (AggregateException ae)
            {
                Exception ex = ae.InnerException;
                while (ex is AggregateException) ex = ex.InnerException;
                throw ex;
            }
            Assert.Fail("Exception should have been raised");
        }


        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        [ExpectedExceptionAttribute(typeof(InvalidCastException))]
        public void FailSideCastAfterContinueWith()
        {
            // GeneratorTestDerivedGrain1Reference extends GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference extends GeneratorTestGrainReference
            try
            {
                IGeneratorTestDerivedGrain1 grain = GeneratorTestDerivedGrain1Factory.GetGrain(GetRandomGrainId());
                IGeneratorTestDerivedGrain2 cast = null;
                Task<bool> av = grain.StringIsNullOrEmpty();
                Task<bool> av2 = av.ContinueWith((Task<bool> t) => Assert.IsTrue(t.Result)).ContinueWith((_AppDomain) =>
                {
                    cast = GeneratorTestDerivedGrain2Factory.Cast(grain);
                }).ContinueWith( (_) => cast.StringConcat("a", "b", "c")).ContinueWith( (_) => cast.StringIsNullOrEmpty().Result);
                Assert.IsFalse(av2.Result);
            }
            catch (AggregateException ae)
            {
                Exception ex = ae.InnerException;
                while (ex is AggregateException) ex = ex.InnerException;
                throw ex;
            }
            Assert.Fail("Exception should have been raised");
        }

        //[TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        [TestMethod, TestCategory("Cast")]
        [ExpectedExceptionAttribute(typeof(InvalidCastException))]
        public void CastFailGrainRefCastFromOtherGrain()
        {
            try
            {
                GrainReference grain = (GrainReference)GeneratorTestGrainFactory.GetGrain(GetRandomGrainId());
                GrainReference cast = (GrainReference)SimpleGrainFactory.Cast(grain);
            }
            catch (AggregateException ae)
            {
                throw ae.InnerException;
            }
            Assert.Fail("Exception should have been raised");
        }

        //[TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        [TestMethod, TestCategory("Cast")]
        [ExpectedExceptionAttribute(typeof(InvalidCastException))]
        public void CastFailGrainRefSideCastFromPeer1()
        {
            // GeneratorTestDerivedGrain1Reference extends GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference extends GeneratorTestGrainReference
            try
            {
                IAddressable grain1 = GeneratorTestDerivedGrain1Factory.GetGrain(GetRandomGrainId());
                GrainReference cast = (GrainReference)GeneratorTestDerivedGrain2Factory.Cast(grain1);
            }
            catch (AggregateException ae)
            {
                throw ae.InnerException;
            }
            Assert.Fail("Exception should have been raised");
        }

        //[TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        [TestMethod, TestCategory("Cast")]
        [ExpectedExceptionAttribute(typeof(InvalidCastException))]
        public void CastFailGrainRefSideCastFromPeer2()
        {
            // GeneratorTestDerivedGrain1Reference extends GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference extends GeneratorTestGrainReference
            try
            {
                IAddressable grain2 = GeneratorTestDerivedGrain2Factory.GetGrain(GetRandomGrainId());
                GrainReference cast = (GrainReference)GeneratorTestDerivedGrain1Factory.Cast(grain2);
            }
            catch (AggregateException ae)
            {
                throw ae.InnerException;
            }
            Assert.Fail("Exception should have been raised");
        }

        //[TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        [TestMethod, TestCategory("Cast")]
        [ExpectedExceptionAttribute(typeof(InvalidCastException))]
        public void CastFailGrainRefDownCastFromParent()
        {
            // GeneratorTestDerivedDerivedGrainReference extends GeneratorTestDerivedGrain2Reference
            // GeneratorTestDerivedGrain2Reference extends GeneratorTestGrainReference
            try
            {
                GrainReference grain = (GrainReference)GeneratorTestDerivedGrain2Factory.GetGrain(GetRandomGrainId());
                GrainReference cast = (GrainReference)GeneratorTestDerivedDerivedGrainFactory.Cast(grain);
            }
            catch (AggregateException ae)
            {
                throw ae.InnerException;
            }
            Assert.Fail("Exception should have been raised");
        }

        //[TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        [TestMethod, TestCategory("Cast")]
        [ExpectedExceptionAttribute(typeof(InvalidCastException))]
        public void CastFailGrainRefDownCastFromGrandparent()
        {
            // GeneratorTestDerivedDerivedGrainReference extends GeneratorTestDerivedGrain2Reference
            // GeneratorTestDerivedGrain2Reference extends GeneratorTestGrainReference
            try
            {
                GrainReference grain = (GrainReference)GeneratorTestGrainFactory.GetGrain(GetRandomGrainId());
                GrainReference cast = (GrainReference)GeneratorTestDerivedDerivedGrainFactory.Cast(grain);
            }
            catch (AggregateException ae)
            {
                throw ae.InnerException;
            }
            Assert.Fail("Exception should have been raised");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void CastGrainRefUpCastFromGrandchild()
        {
            // GeneratorTestDerivedGrain1Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedDerivedGrainReference derives from GeneratorTestDerivedGrain2Reference

            GrainReference cast;
            GrainReference grain = (GrainReference) GeneratorTestDerivedDerivedGrainFactory.GetGrain(GetRandomGrainId());
  
            // Parent
            cast = (GrainReference) GeneratorTestDerivedGrain2Factory.Cast(grain);
            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestDerivedDerivedGrain));
            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestDerivedGrain2));
            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestGrain));
            
            // Cross-cast outside the inheritance hierarchy should not work
            Assert.IsNotInstanceOfType(cast, typeof(IGeneratorTestDerivedGrain1));

            // Grandparent
            cast = (GrainReference) GeneratorTestGrainFactory.Cast(grain);
            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestDerivedDerivedGrain));
            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestGrain));

            // Cross-cast outside the inheritance hierarchy should not work
            Assert.IsNotInstanceOfType(cast, typeof(IGeneratorTestDerivedGrain1));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void CastGrainRefUpCastFromDerivedDerivedChild()
        {
            // GeneratorTestDerivedDerivedGrainReference extends GeneratorTestDerivedGrain2Reference
            // GeneratorTestDerivedGrain2Reference extends GeneratorTestGrainReference
            GrainReference grain = (GrainReference) GeneratorTestDerivedDerivedGrainFactory.GetGrain(GetRandomGrainId());
            GrainReference cast = (GrainReference) GeneratorTestDerivedGrain2Factory.Cast(grain);
            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestDerivedDerivedGrain));
            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestDerivedGrain2));
            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestGrain));
            Assert.IsNotInstanceOfType(cast, typeof(IGeneratorTestDerivedGrain1));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void CastAsyncGrainRefCastFromSelf()
        {
            IAddressable grain = TestConstants.GetSimpleGrain();
            ISimpleGrain cast = SimpleGrainFactory.Cast(grain);

            Task<int> successfulCallPromise = cast.GetA();
            successfulCallPromise.Wait();
            Assert.AreEqual(TaskStatus.RanToCompletion, successfulCallPromise.Status);
        }


        // todo: implement white box access
#if TODO
        [TestMethod]
        public void CastAsyncGrainRefUpCastFromChild()
        {
            // GeneratorTestDerivedGrain1Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedDerivedGrainReference derives from GeneratorTestDerivedGrain2Reference

            //GrainReference grain = GeneratorTestDerivedGrain1Reference.GetGrain(GetRandomGrainId());
            var lookupPromise = GrainReference.CreateGrain(
                "",
                "GeneratorTestGrain.GeneratorTestDerivedGrain1" );
            GrainReference grain = new GrainReference(lookupPromise);

            GrainReference cast = (GrainReference) GeneratorTestGrainFactory.Cast(grain);
            Assert.IsNotNull(cast);
            //Assert.AreSame(typeof(IGeneratorTestGrain), cast.GetType());

            if (!cast.IsResolved)
            {
                cast.Wait(100);  // Resolve the grain reference
            }

            Assert.IsTrue(cast.IsResolved);
            Assert.IsTrue(grain.IsResolved);
        }

        [TestMethod]
        public void CastAsyncGrainRefUpCastFromGrandchild()
        {
            // GeneratorTestDerivedGrain1Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedDerivedGrainReference derives from GeneratorTestDerivedGrain2Reference

            //GrainReference grain = GeneratorTestDerivedDerivedGrainReference.GetGrain(GetRandomGrainId());
            var lookupPromise = GrainReference.CreateGrain(
                "", 
                "GeneratorTestGrain.GeneratorTestDerivedDerivedGrain"
            );
            GrainReference grain = new GrainReference(lookupPromise);

            GrainReference cast = (GrainReference) GeneratorTestGrainFactory.Cast(grain);
            Assert.IsNotNull(cast);
            //Assert.AreSame(typeof(IGeneratorTestGrain), cast.GetType());

            if (!cast.IsResolved)
            {
                cast.Wait(100);  // Resolve the grain reference
            }

            Assert.IsTrue(cast.IsResolved);
            Assert.IsTrue(grain.IsResolved);
        }

        [TestMethod]
        [ExpectedExceptionAttribute(typeof(InvalidCastException))]
        public void CastAsyncGrainRefFailSideCastToPeer()
        {
            // GeneratorTestDerivedGrain1Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedDerivedGrainReference derives from GeneratorTestDerivedGrain2Reference

            //GrainReference grain = GeneratorTestDerivedGrain1Reference.GetGrain(GetRandomGrainId());
            var lookupPromise = GrainReference.CreateGrain(
                "", 
                "GeneratorTestGrain.GeneratorTestDerivedGrain1"
            );
            GrainReference grain = new GrainReference(lookupPromise);

            GrainReference cast = (GrainReference) GeneratorTestDerivedGrain2Factory.Cast(grain);
            if (!cast.IsResolved)
            {
                cast.Wait(100);  // Resolve the grain reference
            }

            Assert.Fail("Exception should have been raised");
        }

        [TestMethod]
        [ExpectedExceptionAttribute(typeof(InvalidCastException))]
        public void CastAsyncGrainRefFailDownCastToChild()
        {
            // GeneratorTestDerivedGrain1Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedDerivedGrainReference derives from GeneratorTestDerivedGrain2Reference

            //GrainReference grain = GeneratorTestGrainReference.GetGrain(GetRandomGrainId());
            var lookupPromise = GrainReference.CreateGrain(
                "", 
                "GeneratorTestGrain.GeneratorTestGrain");
            GrainReference grain = new GrainReference(lookupPromise);

            GrainReference cast = (GrainReference) GeneratorTestDerivedGrain1Factory.Cast(grain);
            if (!cast.IsResolved)
            {
                cast.Wait(100);  // Resolve the grain reference
            }

            Assert.Fail("Exception should have been raised");
        }

        [TestMethod]
        [ExpectedExceptionAttribute(typeof(InvalidCastException))]
        public void CastAsyncGrainRefFailDownCastToGrandchild()
        {
            // GeneratorTestDerivedGrain1Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedDerivedGrainReference derives from GeneratorTestDerivedGrain2Reference

            //GrainReference grain = GeneratorTestGrainReference.GetGrain(GetRandomGrainId());
            var lookupPromise = GrainReference.CreateGrain(
                "", 
                "GeneratorTestGrain.GeneratorTestGrain");
            GrainReference grain = new GrainReference(lookupPromise);

            GrainReference cast = (GrainReference) GeneratorTestDerivedDerivedGrainFactory.Cast(grain);
            if (!cast.IsResolved)
            {
                cast.Wait(100);  // Resolve the grain reference
            }

            Assert.Fail("Exception should have been raised");
        }
#endif
        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Cast")]
        public void CastCallMethodInheritedFromBaseClass()
        {
            // GeneratorTestDerivedGrain1Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedDerivedGrainReference derives from GeneratorTestDerivedGrain2Reference

            Task<bool> isNullStr;

            IGeneratorTestDerivedGrain1 grain = GeneratorTestDerivedGrain1Factory.GetGrain(GetRandomGrainId());
            isNullStr = grain.StringIsNullOrEmpty();
            Assert.IsTrue(isNullStr.Result, "Value should be null initially");

            isNullStr = grain.StringSet("a").ContinueWith((_) => grain.StringIsNullOrEmpty()).Unwrap();
            Assert.IsFalse(isNullStr.Result, "Value should not be null after SetString(a)");

            isNullStr = grain.StringSet(null).ContinueWith((_) => grain.StringIsNullOrEmpty()).Unwrap();
            Assert.IsTrue(isNullStr.Result, "Value should be null after SetString(null)");

            IGeneratorTestGrain cast = GeneratorTestGrainFactory.Cast(grain);
            isNullStr = cast.StringSet("b").ContinueWith((_) => grain.StringIsNullOrEmpty()).Unwrap();
            Assert.IsFalse(isNullStr.Result, "Value should not be null after cast.SetString(b)");
        }
    }
}
