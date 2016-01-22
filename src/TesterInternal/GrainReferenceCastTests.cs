using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;
using GrainInterfaceData = Orleans.CodeGeneration.GrainInterfaceData;

namespace UnitTests
{
    [TestClass]
    public class GrainReferenceCastTests : HostedTestClusterEnsureDefaultStarted
    {
        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        public void CastGrainRefCastFromMyType()
        {
            GrainReference grain = (GrainReference)GrainClient.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), SimpleGrain.SimpleGrainNamePrefix);
            GrainReference cast = (GrainReference)grain.AsReference<ISimpleGrain>();
            Assert.IsInstanceOfType(cast, grain.GetType());
            Assert.IsInstanceOfType(cast, typeof(ISimpleGrain));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        public void CastGrainRefCastFromMyTypePolymorphic()
        {
            // MultifacetTestGrain implements IMultifacetReader
            // MultifacetTestGrain implements IMultifacetWriter
            IAddressable grain = GrainClient.GrainFactory.GetGrain<IMultifacetTestGrain>(0);
            Assert.IsInstanceOfType(grain, typeof(IMultifacetWriter));
            Assert.IsInstanceOfType(grain, typeof(IMultifacetReader));

            IAddressable cast = grain.AsReference<IMultifacetReader>();
            Assert.IsInstanceOfType(cast, grain.GetType());
            Assert.IsInstanceOfType(cast, typeof(IMultifacetWriter));
            Assert.IsInstanceOfType(grain, typeof(IMultifacetReader));

            IAddressable cast2 = grain.AsReference<IMultifacetReader>();
            Assert.IsInstanceOfType(cast2, grain.GetType());
            Assert.IsInstanceOfType(cast2, typeof(IMultifacetReader));
            Assert.IsInstanceOfType(grain, typeof(IMultifacetWriter));
        }

        // Test case currently fails intermittently
        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        public void CastMultifacetRWReference()
        {
            // MultifacetTestGrain implements IMultifacetReader
            // MultifacetTestGrain implements IMultifacetWriter
            int newValue = 3;

            IMultifacetWriter writer = GrainClient.GrainFactory.GetGrain<IMultifacetWriter>(1);
            // No Wait in this test case

            IMultifacetReader reader = writer.AsReference<IMultifacetReader>();  // --> Test case intermittently fails here
            // Error: System.InvalidCastException: Grain reference MultifacetGrain.MultifacetWriterFactory+MultifacetWriterReference service interface mismatch: expected interface id=[1947430462] received interface name=[MultifacetGrain.IMultifacetWriter] id=[62435819] in grainRef=[GrainReference:*std/b198f19f]

            writer.SetValue(newValue).Wait();

            Task<int> readAsync = reader.GetValue();
            readAsync.Wait();
            int result = readAsync.Result;

            Assert.AreEqual(newValue, result);
        }

        // Test case currently fails
        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        public void CastMultifacetRWReferenceWaitForResolve()
        {
            // MultifacetTestGrain implements IMultifacetReader
            // MultifacetTestGrain implements IMultifacetWriter

            //Interface Id values for debug:
            // IMultifacetWriter = 62435819
            // IMultifacetReader = 1947430462
            // IMultifacetTestGrain = 222717230 (also compatable with 1947430462 or 62435819)

            int newValue = 4;

            IMultifacetWriter writer = GrainClient.GrainFactory.GetGrain<IMultifacetWriter>(2);
            
            IMultifacetReader reader = writer.AsReference<IMultifacetReader>(); // --> Test case fails here
            // Error: System.InvalidCastException: Grain reference MultifacetGrain.MultifacetWriterFactory+MultifacetWriterReference service interface mismatch: expected interface id=[1947430462] received interface name=[MultifacetGrain.IMultifacetWriter] id=[62435819] in grainRef=[GrainReference:*std/8408c2bc]
            
            writer.SetValue(newValue).Wait();

            int result = reader.GetValue().Result;

            Assert.AreEqual(newValue, result);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
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

        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        public void CastCheckExpectedCompatIds()
        {
            Type t = typeof(ISimpleGrain);
            int expectedInterfaceId = GrainInterfaceData.GetGrainInterfaceId(t);
            GrainReference grain = (GrainReference)GrainClient.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), SimpleGrain.SimpleGrainNamePrefix);
            Assert.IsTrue(grain.IsCompatible(expectedInterfaceId));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
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
            GrainReference grain = (GrainReference) GrainClient.GrainFactory.GetGrain<IGeneratorTestDerivedDerivedGrain>(GetRandomGrainId());
            Assert.IsTrue(grain.IsCompatible(id1));
            Assert.IsTrue(grain.IsCompatible(id2));
            Assert.IsTrue(grain.IsCompatible(id3));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        [ExpectedException(typeof(InvalidCastException))]
        public void CastFailInternalCastFromBadType()
        {
            Type t = typeof(ISimpleGrain);
            GrainReference grain = (GrainReference)GrainClient.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), SimpleGrain.SimpleGrainNamePrefix);
            IAddressable cast = GrainReference.CastInternal(
                typeof(Boolean),
                null,
                grain,
                GrainInterfaceData.GetGrainInterfaceId(t));
            Assert.Fail("Exception should have been raised");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        public void CastInternalCastFromMyType()
        {
            var serviceName = typeof(SimpleGrain).FullName;
            GrainReference grain = (GrainReference)GrainClient.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), SimpleGrain.SimpleGrainNamePrefix);
            
            IAddressable cast = GrainReference.CastInternal(
                typeof(ISimpleGrain),
                (GrainReference gr) => { throw new InvalidOperationException("Should not need to create a new GrainReference wrapper"); },
                grain,
                Utils.CalculateIdHash(serviceName));

            Assert.IsInstanceOfType(cast, typeof(ISimpleGrain));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        public void CastInternalCastUpFromChild()
        {
            // GeneratorTestDerivedGrain1Reference extends GeneratorTestGrainReference
            GrainReference grain = (GrainReference) GrainClient.GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());
            
            var serviceName = typeof(GeneratorTestGrain).FullName;
            IAddressable cast = GrainReference.CastInternal(
                typeof(IGeneratorTestGrain),
                (GrainReference gr) => { throw new InvalidOperationException("Should not need to create a new GrainReference wrapper"); },
                grain,
                Utils.CalculateIdHash(serviceName));

            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestGrain));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        public void CastGrainRefUpCastFromChild()
        {
            // GeneratorTestDerivedGrain1Reference extends GeneratorTestGrainReference
            GrainReference grain = (GrainReference) GrainClient.GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());
            GrainReference cast = (GrainReference) grain.AsReference<IGeneratorTestGrain>();
            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestDerivedGrain1));
            Assert.IsInstanceOfType(cast,typeof(IGeneratorTestGrain));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        [ExpectedException(typeof(InvalidCastException))]
        public void FailSideCastAfterResolve()
        {
            // GeneratorTestDerivedGrain1Reference extends GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference extends GeneratorTestGrainReference
            try
            {
                IGeneratorTestDerivedGrain1 grain = GrainClient.GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());
                Assert.IsTrue(grain.StringIsNullOrEmpty().Result);
                // Fails the next line as grain reference is already resolved
                IGeneratorTestDerivedGrain2 cast = grain.AsReference<IGeneratorTestDerivedGrain2>();
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

        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        [ExpectedException(typeof(InvalidCastException))]
        public void FailOperationAfterSideCast()
        {
            // GeneratorTestDerivedGrain1Reference extends GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference extends GeneratorTestGrainReference
            try
            {
                IGeneratorTestDerivedGrain1 grain = GrainClient.GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());
                // Cast works optimistically when the grain reference is not already resolved
                IGeneratorTestDerivedGrain2 cast = grain.AsReference<IGeneratorTestDerivedGrain2>();
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


        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        [ExpectedException(typeof(InvalidCastException))]
        public void FailSideCastAfterContinueWith()
        {
            // GeneratorTestDerivedGrain1Reference extends GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference extends GeneratorTestGrainReference
            try
            {
                IGeneratorTestDerivedGrain1 grain = GrainClient.GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());
                IGeneratorTestDerivedGrain2 cast = null;
                Task<bool> av = grain.StringIsNullOrEmpty();
                Task<bool> av2 = av.ContinueWith((Task<bool> t) => Assert.IsTrue(t.Result)).ContinueWith((_AppDomain) =>
                {
                    cast = grain.AsReference<IGeneratorTestDerivedGrain2>();
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

        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        public void CastGrainRefUpCastFromGrandchild()
        {
            // GeneratorTestDerivedGrain1Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedDerivedGrainReference derives from GeneratorTestDerivedGrain2Reference

            GrainReference cast;
            GrainReference grain = (GrainReference)GrainClient.GrainFactory.GetGrain<IGeneratorTestDerivedDerivedGrain>(GetRandomGrainId());
  
            // Parent
            cast = (GrainReference) grain.AsReference<IGeneratorTestDerivedGrain2>();
            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestDerivedDerivedGrain));
            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestDerivedGrain2));
            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestGrain));
            
            // Cross-cast outside the inheritance hierarchy should not work
            Assert.IsNotInstanceOfType(cast, typeof(IGeneratorTestDerivedGrain1));

            // Grandparent
            cast = (GrainReference) grain.AsReference<IGeneratorTestGrain>();
            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestDerivedDerivedGrain));
            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestGrain));

            // Cross-cast outside the inheritance hierarchy should not work
            Assert.IsNotInstanceOfType(cast, typeof(IGeneratorTestDerivedGrain1));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        public void CastGrainRefUpCastFromDerivedDerivedChild()
        {
            // GeneratorTestDerivedDerivedGrainReference extends GeneratorTestDerivedGrain2Reference
            // GeneratorTestDerivedGrain2Reference extends GeneratorTestGrainReference
            GrainReference grain = (GrainReference) GrainClient.GrainFactory.GetGrain<IGeneratorTestDerivedDerivedGrain>(GetRandomGrainId());
            GrainReference cast = (GrainReference) grain.AsReference<IGeneratorTestDerivedGrain2>();
            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestDerivedDerivedGrain));
            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestDerivedGrain2));
            Assert.IsInstanceOfType(cast, typeof(IGeneratorTestGrain));
            Assert.IsNotInstanceOfType(cast, typeof(IGeneratorTestDerivedGrain1));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        public void CastAsyncGrainRefCastFromSelf()
        {
            IAddressable grain = GrainClient.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), SimpleGrain.SimpleGrainNamePrefix); ;
            ISimpleGrain cast = grain.AsReference<ISimpleGrain>();

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
        [TestMethod, TestCategory("Functional"), TestCategory("Cast")]
        public void CastCallMethodInheritedFromBaseClass()
        {
            // GeneratorTestDerivedGrain1Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedGrain2Reference derives from GeneratorTestGrainReference
            // GeneratorTestDerivedDerivedGrainReference derives from GeneratorTestDerivedGrain2Reference

            Task<bool> isNullStr;

            IGeneratorTestDerivedGrain1 grain = GrainClient.GrainFactory.GetGrain<IGeneratorTestDerivedGrain1>(GetRandomGrainId());
            isNullStr = grain.StringIsNullOrEmpty();
            Assert.IsTrue(isNullStr.Result, "Value should be null initially");

            isNullStr = grain.StringSet("a").ContinueWith((_) => grain.StringIsNullOrEmpty()).Unwrap();
            Assert.IsFalse(isNullStr.Result, "Value should not be null after SetString(a)");

            isNullStr = grain.StringSet(null).ContinueWith((_) => grain.StringIsNullOrEmpty()).Unwrap();
            Assert.IsTrue(isNullStr.Result, "Value should be null after SetString(null)");

            IGeneratorTestGrain cast = grain.AsReference<IGeneratorTestGrain>();
            isNullStr = cast.StringSet("b").ContinueWith((_) => grain.StringIsNullOrEmpty()).Unwrap();
            Assert.IsFalse(isNullStr.Result, "Value should not be null after cast.SetString(b)");
        }
    }
}
