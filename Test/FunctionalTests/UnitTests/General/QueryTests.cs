using Microsoft.VisualStudio.TestTools.UnitTesting;

#pragma warning disable 618

#if DEBUG || REVISIT

namespace UnitTests.General
{
    [TestClass]
    public class QueryTests : UnitTestBase
    {
        public QueryTests()
//            : base(new Options {StartFreshOrleans = true, UseStore=true, IndexConnection=null, ClearIndexOnStartup=true})
        {
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

//        [TestMethod, TestCategory("Nightly"), TestCategory("General")]
//        public void QueryRows()
//        {
//            var row1 = RowGrainFactory.CreateGrain(Sku: "a1", Name: "Apple", Quantity: 10, Price: 0.50);
//            var row2 = RowGrainFactory.CreateGrain(Sku: "b1", Name: "Banana", Quantity: 4, Price: 0.10);
//            var row3 = RowGrainFactory.CreateGrain(Sku: "b2", Name: "Car", Quantity: 1, Price: 10000.0);
//            row1.Wait();
//            row2.Wait();
//            row3.Wait();
//            logger.Info("a1 {0} b1 {1} b2 {2}", ((GrainReference)row1).GrainId, ((GrainReference)row2).GrainId, ((GrainReference)row3).GrainId);

//            var a = RowGrainFactory.Where(x => x.Sku == "a1").Result.Single();
//            var name = a.Name.Result;
//            var quantity = a.Quantity.Result;
//            var price = a.Price.Result;
//            Assert.IsTrue(name == "Apple" && quantity == 10 && price == 0.50,
//                String.Format("Should be Apple 10 0.5, is {0} {1} {2}", name, quantity, price));

//            var b = RowGrainFactory.Where(x => x.Sku.StartsWith("b")).Result
//                .Select(row => row.Name.Result).ToArray();
//            Assert.IsTrue(b.Length == 2 && b.Contains("Banana") && b.Contains("Car"),
//                String.Format("Should be Banana Car is {0}", b.ToStrings()));
//        }

//        [TestMethod, TestCategory("Nightly"), TestCategory("General")]
//        public void DuplicatePrimaryKeys()
//        {
//            IRowGrain a = RowGrainFactory.CreateGrain(Sku: "a", Name: "Apple", Quantity: 10, Price: 0.50);
//            a.Wait();
//            IRowGrain a2 = RowGrainFactory.CreateGrain(Sku: "a", Name: "Apple", Quantity: 20, Price: 0.50);
//            bool ok = true;
//            a2.ContinueWith(() => { ok = true; }, ex => { ok = false; }).Wait();
//#if FALSE
//            try
//            {
//                a2.Wait();
//            }
//            catch (Exception)
//            {
//                ok = false;
//            }
//#endif
//            Assert.IsFalse(ok, "Should fail to create duplicate primary key");
//            IRowGrain a3 = RowGrainFactory.LookupSku("a");
//            int q2 = a3.Quantity.Result;
//            Assert.AreEqual(q2, 10, "Should have value from first Create");
//        }

//        [TestMethod]
//        public void LookupOrCreate()
//        {
//            var j = RowGrainFactory.CreateGrain(Sku: "j", Name: "Jam");
//            var j2 = RowGrainFactory.LookupSkuOrCreate("j");
//            Assert.AreEqual("Jam", j2.Name.Result);

//            var k = RowGrainFactory.LookupSkuOrCreate("k");
//            Assert.AreEqual(null, k.Name.Result);
//            k.SetName("Kiwi");

//            var k2 = RowGrainFactory.LookupSkuOrCreate("k");
//            Assert.AreEqual("Kiwi", k2.Name.Result);
//        }

//        [TestMethod, TestCategory("Nightly"), TestCategory("General")]
//        public void DeleteGrainTest()
//        {
//            var x = RowGrainFactory.CreateGrain(Sku: "x", Quantity: 10, Price: 1.00);
//            var y = RowGrainFactory.CreateGrain(Sku: "y", Quantity: 10, Price: 1.00);
//            x.Wait();
//            RowGrainFactory.Delete(x).Wait();
//            var xs = RowGrainFactory.Where(g => g.Sku == "x").Result.ToList();
//            Assert.IsTrue(xs.Count == 0, "Should not find deleted grain");
//            y.Wait();
//            var ys = RowGrainFactory.Where(g => g.Sku == "y").Result.ToList();
//            Assert.IsTrue(ys.Count == 1, "Should find non-deleted grain");
//            try
//            {
//                var x2 = RowGrainFactory.CreateGrain(Sku: "x", Quantity: 10, Price: 1.00);
//                x2.Wait();
//            }
//            catch (Exception e)
//            {
//                Assert.Fail("Should have been able to re-create deleted grain, failed with " + e);
//            }
//        }

//        [TestMethod, TestCategory("Nightly"), TestCategory("General")]
//        public void DeleteWhereTest()
//        {
//            var z1 = RowGrainFactory.CreateGrain(Sku: "z1", Quantity: 10, Price: 1.00);
//            var z2 = RowGrainFactory.CreateGrain(Sku: "z2", Quantity: 10, Price: 1.00);
//            var z21 = RowGrainFactory.CreateGrain(Sku: "z21", Quantity: 10, Price: 1.00);
//            z1.Wait();
//            z2.Wait();
//            z21.Wait();
//            RowGrainFactory.DeleteWhere(g => g.Sku.StartsWith("z2")).Wait();
//            var z2s = RowGrainFactory.Where(g => g.Sku.StartsWith("z2")).Result.ToList();
//            Assert.IsTrue(z2s.Count == 0, "Should not find deleted grains");
//            var z1s = RowGrainFactory.Where(g => g.Sku == "z1").Result.ToList();
//            Assert.IsTrue(z1s.Count == 1, "Should find non-deleted grain");
//            try
//            {
//                var z22 = RowGrainFactory.CreateGrain(Sku: "z2", Quantity: 10, Price: 1.00);
//                z22.Wait();
//            }
//            catch (Exception e)
//            {
//                Assert.Fail("Should have been able to re-create deleted grain, failed with " + e);
//            }
//        }
    }
}

#endif

#pragma warning restore 618
