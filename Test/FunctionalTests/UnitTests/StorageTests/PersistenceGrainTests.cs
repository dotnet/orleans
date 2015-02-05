//#define REREAD_STATE_AFTER_WRITE_FAILED
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.AzureUtils;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.CodeGeneration;
using Echo;
using UnitTestGrainInterfaces;
using UnitTestGrains;

// ReSharper disable RedundantAssignment
// ReSharper disable UnusedVariable
// ReSharper disable InconsistentNaming

namespace UnitTests.StorageTests
{
    /// <summary>
    /// PersistenceGrainTests - Run with only local unit test silo -- no external dependency on Azure storage
    /// </summary>
    [TestClass]
    [DeploymentItem("Config_DevStorage.xml")]
    public class PersistenceGrainTests_Local : UnitTestBase
    {
        const string ErrorInjectorStorageProvider = "ErrorInjector";

        private static readonly Options testSiloOptions = new Options
        {
            SiloConfigFile = new FileInfo("Config_DevStorage.xml"),
            StartFreshOrleans = true,
            StartPrimary = true,
            StartSecondary = false,
        };

        public PersistenceGrainTests_Local()
            : base(testSiloOptions)
        {
            AppDomain.CurrentDomain.UnhandledException += ReportUnhandledException;
            TaskScheduler.UnobservedTaskException += ReportUnobservedTaskException;
        }

        private static void ReportUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Console.WriteLine("Unhandled exception left behind: {0}", e.ExceptionObject);
        }

        private static void ReportUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs excArgs)
        {
            Console.WriteLine("Unhandled Task exception left behind: {0}", excArgs.Exception);
            excArgs.SetObserved();
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            ResetDefaultRuntimes();
            AppDomain.CurrentDomain.UnhandledException -= ReportUnhandledException;
            TaskScheduler.UnobservedTaskException -= ReportUnobservedTaskException;
        }

        [TestInitialize]
        public void TestInitialize()
        {
            SetErrorInjection(ErrorInjectorStorageProvider, ErrorInjectionPoint.None);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            SetErrorInjection(ErrorInjectorStorageProvider, ErrorInjectionPoint.None);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence")]
        public void Persistence_Silo_StorageProviders()
        {
            List<SiloHandle> silos = GetActiveSilos().ToList();
            foreach (var silo in silos)
            {
                List<string> providers = silo.Silo.TestHookup.GetStorageProviderNames().ToList();
                Assert.IsNotNull(providers, "Null provider manager");
                Assert.IsTrue(providers.Count > 0, "Some providers loaded");
                const string providerName = "test1";
                IStorageProvider store = silo.Silo.TestHookup.GetStorageProvider(providerName);
                Assert.IsNotNull(store, "No storage provider found: {0}", providerName);
            }
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence")]
        public void Persistence_Silo_StorageProvider_Name_LowerCase()
        {
            List<SiloHandle> silos = GetActiveSilos().ToList();
            foreach (var silo in silos)
            {
                const string providerName = "LowerCase";
                IStorageProvider store = silo.Silo.TestHookup.GetStorageProvider(providerName);
                Assert.IsNotNull(store, "No storage provider found: {0}", providerName);
            }
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence")]
        [ExpectedException(typeof(KeyNotFoundException))]
        public void Persistence_Silo_StorageProvider_Name_Missing()
        {
            List<SiloHandle> silos = GetActiveSilos().ToList();
            var silo = silos.First();
            const string providerName = "NotPresent";
            IStorageProvider store = silo.Silo.TestHookup.GetStorageProvider(providerName);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Grain_CheckStateInit()
        {
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = PersistenceTestGrainFactory.GetGrain(id);
            bool ok = await grain.CheckStateInit();
            Assert.IsTrue(ok, "CheckStateInit OK");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Grain_CheckStorageProvider()
        {
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = PersistenceTestGrainFactory.GetGrain(id);
            string providerType = await grain.CheckProviderType();
            Assert.AreEqual(typeof(MockStorageProvider).FullName, providerType, "StorageProvider provider type");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Init()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = PersistenceTestGrainFactory.GetGrain(id);

            await grain.DoSomething();

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.AreEqual(1, storageProvider.InitCount, "StorageProvider #Init");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Activate_StoredValue()
        {
            const string providerName = "test1";
            string grainType = typeof(PersistenceTestGrain).FullName;
            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");

            IPersistenceTestGrain grain = PersistenceTestGrainFactory.GetGrain(guid);

            // Store initial value in storage
            int initialValue = 567;
            SetStoredValue(providerName, grainType, grain, "Field1", initialValue);

            int readValue = await grain.GetValue();
            Assert.AreEqual(initialValue, readValue, "Read previously stored value");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Activate_Error()
        {
            const string providerName = ErrorInjectorStorageProvider;
            string grainType = typeof(PersistenceProviderErrorGrain).FullName;
            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");

            IPersistenceProviderErrorGrain grain = PersistenceProviderErrorGrainFactory.GetGrain(guid);

            // Store initial value in storage
            int initialValue = 567;
            SetStoredValue(providerName, grainType, grain, "Field1", initialValue);

            SetErrorInjection(providerName, ErrorInjectionPoint.BeforeRead);

            bool exceptionThrown = false;
            try
            {
                int readValue = await grain.GetValue();
            }
            catch (ApplicationException)
            {
                exceptionThrown = true;
                // Expected error
            }
            catch (AggregateException ae)
            {
                exceptionThrown = true;
                Exception e = ae.GetBaseException();
                if (e is ApplicationException)
                {
                    // Expected error
                }
                else
                {
                    throw e;
                }
            }
            SetErrorInjection(providerName, ErrorInjectionPoint.None);
            if (!exceptionThrown)
            {
                Assert.Fail("Exception should have been thrown during Activate");
            }
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Read()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = PersistenceTestGrainFactory.GetGrain(id);

            await grain.DoSomething();

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(0, storageProvider.WriteCount, "StorageProvider #Writes");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Write()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = PersistenceTestGrainFactory.GetGrain(id);

            await grain.DoWrite(1);

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(1, storageProvider.WriteCount, "StorageProvider #Writes");

            Assert.AreEqual(1, storageProvider.LastState["Field1"], "Store-Field1");

            await grain.DoWrite(2);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(2, storageProvider.WriteCount, "StorageProvider #Writes");

            Assert.AreEqual(2, storageProvider.LastState["Field1"], "Store-Field1");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Grain_ReRead()
        {
            const string providerName = "test1";
            string grainType = typeof(PersistenceTestGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceTestGrain grain = PersistenceTestGrainFactory.GetGrain(guid);

            await grain.DoSomething();

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(0, storageProvider.WriteCount, "StorageProvider #Writes");

            storageProvider.SetValue(grainType, grain.AsReference(), "Field1", 42); // Update state data behind grain

            await grain.DoRead();

            Assert.AreEqual(2, storageProvider.ReadCount, "StorageProvider #Reads-2");
            Assert.AreEqual(0, storageProvider.WriteCount, "StorageProvider #Writes-2");

            Assert.AreEqual(42, storageProvider.LastState["Field1"], "Store-Field1");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public async Task MemoryStore_Read_Write()
        {
            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IMemoryStorageTestGrain grain = MemoryStorageTestGrainFactory.GetGrain(guid);

            int val = await grain.GetValue();

            Assert.AreEqual(0, val, "Initial value");

            await grain.DoWrite(1);
            val = await grain.GetValue();
            Assert.AreEqual(1, val, "Value after Write-1");

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Write-2");

            val = await grain.DoRead();

            Assert.AreEqual(2, val, "Value after Re-Read");
        }

        [TestMethod, TestCategory("Stress"), TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public async Task MemoryStore_Stress_Read()
        {
            const int numIterations = 10000;

            Stopwatch sw = Stopwatch.StartNew();

            Task<int>[] promises = new Task<int>[numIterations];
            for (int i = 0; i < numIterations; i++)
            {
                IMemoryStorageTestGrain grain = MemoryStorageTestGrainFactory.GetGrain(i);
                int idx = i; // Capture
                Func<Task<int>> asyncFunc =
                    async () =>
                    {
                        await grain.DoWrite(idx);
                        return await grain.DoRead();
                    };
                promises[i] = Task.Run(asyncFunc);
            }
            await Task.WhenAll(promises);

            TimeSpan elapsed = sw.Elapsed;
            double tps = (numIterations*2)/elapsed.TotalSeconds; // One Read and one Write per iteration
            Console.WriteLine("{0} Completed {1} Read-Write operations in {2} at {3} TPS", TestContext.TestName, numIterations, elapsed, tps);

            for (int i = 0; i < numIterations; i++)
            {
                int expectedVal = i;
                Assert.AreEqual(expectedVal, promises[i].Result, "Returned value - Read @ #" + i);
            }
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Write()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = PersistenceTestGrainFactory.GetGrain(id);

            await grain.DoWrite(1);

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(1, storageProvider.WriteCount, "StorageProvider #Writes");

            Assert.AreEqual(1, storageProvider.LastState["Field1"], "Store-Field1");

            await grain.DoWrite(2);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(2, storageProvider.WriteCount, "StorageProvider #Writes");

            Assert.AreEqual(2, storageProvider.LastState["Field1"], "Store-Field1");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Delete()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = PersistenceTestGrainFactory.GetGrain(id);

            await grain.DoWrite(1);

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(1, storageProvider.WriteCount, "StorageProvider #Writes");

            await grain.DoDelete();

            Assert.AreEqual(1, storageProvider.DeleteCount, "StorageProvider #Deletes");
            Assert.AreEqual(0, storageProvider.LastState.Count, "Store-AfterDelete-Empty");

            int val = await grain.GetValue(); // Returns current in-memory null data without re-read.
            Assert.AreEqual(0, val, "Value after Delete");
            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");

            await grain.DoWrite(2);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(2, storageProvider.WriteCount, "StorageProvider #Writes");

            Assert.AreEqual(2, storageProvider.LastState["Field1"], "Store-Field1");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Read_Error()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceErrorGrain grain = PersistenceErrorGrainFactory.GetGrain(id);

            var val = await grain.GetValue();

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(0, storageProvider.WriteCount, "StorageProvider #Writes");

            try
            {
                await grain.DoReadError(true);
            }
            catch (ApplicationException)
            {
                // Expected error
            }
            catch (AggregateException ae)
            {
                Exception e = ae.GetBaseException();
                if (e is ApplicationException)
                {
                    // Expected error
                }
                else
                {
                    throw e;
                }
            }

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads-2");
            Assert.AreEqual(0, storageProvider.WriteCount, "StorageProvider #Writes-2");

            try
            {
                await grain.DoReadError(false);
            }
            catch (ApplicationException)
            {
                // Expected error
            }
            catch (AggregateException ae)
            {
                Exception e = ae.GetBaseException();
                if (e is ApplicationException)
                {
                    // Expected error
                }
                else
                {
                    throw e;
                }
            }

            Assert.AreEqual(2, storageProvider.ReadCount, "StorageProvider #Reads-2");
            Assert.AreEqual(0, storageProvider.WriteCount, "StorageProvider #Writes-2");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Write_Error()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceErrorGrain grain = PersistenceErrorGrainFactory.GetGrain(id);

            await grain.DoWrite(1);

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(1, storageProvider.WriteCount, "StorageProvider #Writes");

            Assert.AreEqual(1, storageProvider.LastState["Field1"], "Store-Field1");

            await grain.DoWrite(2);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(2, storageProvider.WriteCount, "StorageProvider #Writes");

            Assert.AreEqual(2, storageProvider.LastState["Field1"], "Store-Field1");

            try
            {
                await grain.DoWriteError(3, true);
            }
            catch (ApplicationException)
            {
                // Expected error
            }
            catch (AggregateException ae)
            {
                if (ae.GetBaseException() is ApplicationException)
                {
                    // Expected error
                }
                else
                {
                    throw;
                }
            }

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(2, storageProvider.WriteCount, "StorageProvider #Writes");

            Assert.AreEqual(2, storageProvider.LastState["Field1"], "Store-Field1");

            try
            {
                await grain.DoWriteError(4, false);
            }
            catch (ApplicationException)
            {
                // Expected error
            }
            catch (AggregateException ae)
            {
                if (ae.GetBaseException() is ApplicationException)
                {
                    // Expected error
                }
                else
                {
                    throw;
                }
            }

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(3, storageProvider.WriteCount, "StorageProvider #Writes");

            Assert.AreEqual(4, storageProvider.LastState["Field1"], "Store-Field1");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Grain_ReRead_Error()
        {
            const string providerName = "test1";
            string grainType = typeof(PersistenceErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceErrorGrain grain = PersistenceErrorGrainFactory.GetGrain(guid);

            var val = await grain.GetValue();

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(0, storageProvider.WriteCount, "StorageProvider #Writes");

            storageProvider.SetValue(grainType, grain.AsReference(), "Field1", 42); // Update state data behind grain

            await grain.DoRead();

            Assert.AreEqual(2, storageProvider.ReadCount, "StorageProvider #Reads-2");
            Assert.AreEqual(0, storageProvider.WriteCount, "StorageProvider #Writes-2");

            Assert.AreEqual(42, storageProvider.LastState["Field1"], "Store-Field1");

            await grain.DoWrite(43);

            Assert.AreEqual(2, storageProvider.ReadCount, "StorageProvider #Reads-2");
            Assert.AreEqual(1, storageProvider.WriteCount, "StorageProvider #Writes-2");

            Assert.AreEqual(43, storageProvider.LastState["Field1"], "Store-Field1");

            try
            {
                await grain.DoReadError(true);
            }
            catch (ApplicationException)
            {
                // Expected error
            }
            catch (AggregateException ae)
            {
                if (ae.GetBaseException() is ApplicationException)
                {
                    // Expected error
                }
                else
                {
                    throw;
                }
            }

            Assert.AreEqual(2, storageProvider.ReadCount, "StorageProvider #Reads-2");
            Assert.AreEqual(1, storageProvider.WriteCount, "StorageProvider #Writes-2");

            Assert.AreEqual(43, storageProvider.LastState["Field1"], "Store-Field1");

            try
            {
                await grain.DoReadError(false);
            }
            catch (ApplicationException)
            {
                // Expected error
            }
            catch (AggregateException ae)
            {
                if (ae.GetBaseException() is ApplicationException)
                {
                    // Expected error
                }
                else
                {
                    throw;
                }
            }

            Assert.AreEqual(3, storageProvider.ReadCount, "StorageProvider #Reads-2");
            Assert.AreEqual(1, storageProvider.WriteCount, "StorageProvider #Writes-2");

            Assert.AreEqual(43, storageProvider.LastState["Field1"], "Store-Field1");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_BeforeRead()
        {
            string grainType = typeof(PersistenceProviderErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceProviderErrorGrain grain = PersistenceProviderErrorGrainFactory.GetGrain(guid);

            var val = await grain.GetValue(); // Activate grain
            int expectedVal = 42;

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            storageProvider.SetValue(grainType, grain.AsReference(), "Field1", expectedVal); // Update state data behind grain

            val = await grain.DoRead();

            Assert.AreEqual(expectedVal, val, "Returned value");

            storageProvider.SetErrorInjection(ErrorInjectionPoint.BeforeRead);
            CheckStorageProviderErrors(grain.DoRead);

            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            val = await grain.GetValue();
            Assert.AreEqual(expectedVal, val, "Returned value");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_AfterRead()
        {
            string grainType = typeof(PersistenceProviderErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceProviderErrorGrain grain = PersistenceProviderErrorGrainFactory.GetGrain(guid);

            var val = await grain.GetValue(); // Activate grain
            int expectedVal = 52;

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            storageProvider.SetValue(grainType, grain.AsReference(), "Field1", expectedVal); // Update state data behind grain

            val = await grain.DoRead();

            Assert.AreEqual(expectedVal, val, "Returned value");

            storageProvider.SetErrorInjection(ErrorInjectionPoint.AfterRead);
            CheckStorageProviderErrors(grain.DoRead);

            val = await grain.GetValue();
            Assert.AreEqual(expectedVal, val, "Returned value");

            int newVal = 53;
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);
            storageProvider.SetValue(grainType, grain.AsReference(), "Field1", newVal); // Update state data behind grain

            val = await grain.GetValue();
            Assert.AreEqual(expectedVal, val, "Returned value");

            await grain.DoRead(); // Force re-read
            expectedVal = newVal;

            val = await grain.GetValue();
            Assert.AreEqual(expectedVal, val, "Returned value");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_BeforeWrite()
        {
            Guid id = Guid.NewGuid();
            IPersistenceProviderErrorGrain grain = PersistenceProviderErrorGrainFactory.GetGrain(id);

            var val = await grain.GetValue();

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);

            int expectedVal = 62;
            await grain.DoWrite(expectedVal);
            Assert.AreEqual(expectedVal, storageProvider.LastState["Field1"], "Store-Field1");

            const int attemptedVal3 = 63;
            storageProvider.SetErrorInjection(ErrorInjectionPoint.BeforeWrite);
            CheckStorageProviderErrors(() => grain.DoWrite(attemptedVal3));
            // Stored value unchanged
            Assert.AreEqual(expectedVal, storageProvider.LastState["Field1"], "Store-Field1");

            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);
            val = await grain.GetValue();
            // Stored value unchanged
            Assert.AreEqual(expectedVal, storageProvider.LastState["Field1"], "Store-Field1");
#if REREAD_STATE_AFTER_WRITE_FAILED
            Assert.AreEqual(expectedVal, val, "Last value written successfully");
#else
            Assert.AreEqual(attemptedVal3, val, "Last value attempted to be written is still in memory");
#endif
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_AfterWrite()
        {
            Guid id = Guid.NewGuid();
            IPersistenceProviderErrorGrain grain = PersistenceProviderErrorGrainFactory.GetGrain(id);

            var val = await grain.GetValue();

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);

            int expectedVal = 82;
            await grain.DoWrite(expectedVal);
            Assert.AreEqual(expectedVal, storageProvider.LastState["Field1"], "Store-Field1");

            const int attemptedVal4 = 83;
            storageProvider.SetErrorInjection(ErrorInjectionPoint.AfterWrite);
            CheckStorageProviderErrors(() => grain.DoWrite(attemptedVal4));

            // Stored value has changed
            expectedVal = attemptedVal4;
            Assert.AreEqual(expectedVal, storageProvider.LastState["Field1"], "Store-Field1");
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);
            val = await grain.GetValue();
            Assert.AreEqual(expectedVal, val, "Returned value");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_BeforeReRead()
        {
            string grainType = typeof(PersistenceProviderErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceProviderErrorGrain grain = PersistenceProviderErrorGrainFactory.GetGrain(guid);

            var val = await grain.GetValue();

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);

            int expectedVal = 72;
            storageProvider.SetValue(grainType, grain.AsReference(), "Field1", expectedVal); // Update state data behind grain
            val = await grain.DoRead();
            Assert.AreEqual(expectedVal, val, "Returned value");

            expectedVal = 73;
            await grain.DoWrite(expectedVal);
            Assert.AreEqual(expectedVal, storageProvider.LastState["Field1"], "Store-Field1");

            storageProvider.SetErrorInjection(ErrorInjectionPoint.BeforeRead);
            CheckStorageProviderErrors(grain.DoRead);

            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);
            val = await grain.GetValue();
            Assert.AreEqual(expectedVal, val, "Returned value");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_AfterReRead()
        {
            string grainType = typeof(PersistenceProviderErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceProviderErrorGrain grain = PersistenceProviderErrorGrainFactory.GetGrain(guid);

            var val = await grain.GetValue();

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);

            int expectedVal = 92;
            storageProvider.SetValue(grainType, grain.AsReference(), "Field1", expectedVal); // Update state data behind grain
            val = await grain.DoRead();
            Assert.AreEqual(expectedVal, val, "Returned value");

            expectedVal = 93;
            await grain.DoWrite(expectedVal);
            Assert.AreEqual(expectedVal, storageProvider.LastState["Field1"], "Store-Field1");

            expectedVal = 94;
            storageProvider.SetValue(grainType, grain.AsReference(), "Field1", expectedVal); // Update state data behind grain
            storageProvider.SetErrorInjection(ErrorInjectionPoint.AfterRead);
            CheckStorageProviderErrors(grain.DoRead);

            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);
            val = await grain.GetValue();
            Assert.AreEqual(expectedVal, val, "Returned value");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Error_Handled_Read()
        {
            string grainType = typeof(PersistenceUserHandledErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceUserHandledErrorGrain grain = PersistenceUserHandledErrorGrainFactory.GetGrain(guid);

            var val = await grain.GetValue(); // Activate grain
            int expectedVal = 42;

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            storageProvider.SetValue(grainType, grain.AsReference(), "Field1", expectedVal); // Update state data behind grain

            val = await grain.DoRead(false);

            Assert.AreEqual(expectedVal, val, "Returned value");

            int newVal = expectedVal + 1;
            storageProvider.SetValue(grainType, grain.AsReference(), "Field1", newVal); // Update state data behind grain
            storageProvider.SetErrorInjection(ErrorInjectionPoint.BeforeRead);
            val = await grain.DoRead(true);
            Assert.AreEqual(expectedVal, val, "Returned value");

            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            expectedVal = newVal;
            storageProvider.SetValue(grainType, grain.AsReference(), "Field1", newVal); // Update state data behind grain
            val = await grain.DoRead(false);
            Assert.AreEqual(expectedVal, val, "Returned value");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Error_Handled_Write()
        {
            string grainType = typeof(PersistenceUserHandledErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceUserHandledErrorGrain grain = PersistenceUserHandledErrorGrainFactory.GetGrain(guid);

            var val = await grain.GetValue(); // Activate grain
            int expectedVal = 42;

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            storageProvider.SetValue(grainType, grain.AsReference(), "Field1", expectedVal); // Update state data behind grain

            val = await grain.DoRead(false);

            Assert.AreEqual(expectedVal, val, "Returned value");

            int newVal = expectedVal + 1;
            storageProvider.SetErrorInjection(ErrorInjectionPoint.BeforeWrite);
            await grain.DoWrite(newVal, true);
            val = await grain.GetValue();
            Assert.AreEqual(expectedVal, val, "Returned value");

            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            expectedVal = newVal;
            await grain.DoWrite(newVal, false);
            val = await grain.GetValue();
            Assert.AreEqual(expectedVal, val, "Returned value");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Error_NotHandled_Write()
        {
            string grainType = typeof(PersistenceUserHandledErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceUserHandledErrorGrain grain = PersistenceUserHandledErrorGrainFactory.GetGrain(guid);

            var val = await grain.GetValue(); // Activate grain
            int expectedVal = 42;

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            storageProvider.SetValue(grainType, grain.AsReference(), "Field1", expectedVal); // Update state data behind grain

            val = await grain.DoRead(false);

            Assert.AreEqual(expectedVal, val, "Returned value after read");

            int newVal = expectedVal + 1;
            storageProvider.SetErrorInjection(ErrorInjectionPoint.BeforeWrite);
            CheckStorageProviderErrors(() => grain.DoWrite(newVal, false));

            val = await grain.GetValue();
            // Stored value unchanged
            Assert.AreEqual(expectedVal, storageProvider.LastState["Field1"], "Store-Field1");
#if REREAD_STATE_AFTER_WRITE_FAILED
            Assert.AreEqual(expectedVal, val, "After failed write: Last value written successfully");
#else
            Assert.AreEqual(newVal, val, "After failed write: Last value attempted to be written is still in memory");
#endif

            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            expectedVal = newVal;
            await grain.DoWrite(newVal, false);
            val = await grain.GetValue();
            Assert.AreEqual(expectedVal, val, "Returned value after good write");
        }

        [TestMethod, TestCategory("Stress"), TestCategory("CorePerf"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Loop_Read()
        {
            const int numIterations = 100;

            string grainType = typeof(PersistenceTestGrain).FullName;

            Task<int>[] promises = new Task<int>[numIterations];
            for (int i = 0; i < numIterations; i++)
            {
                int expectedVal = i;
                IPersistenceTestGrain grain = PersistenceTestGrainFactory.GetGrain(i);
                Guid guid = grain.GetPrimaryKey();
                string id = guid.ToString("N");
                SetStoredValue("test1", grainType, grain, "Field1", expectedVal); // Update state data behind grain
                promises[i] = grain.DoRead();
            }
            await Task.WhenAll(promises);

            for (int i = 0; i < numIterations; i++)
            {
                int expectedVal = i;
                Assert.AreEqual(expectedVal, promises[i].Result, "Returned value - Read @ #" + i);
            }
        }

        [TestMethod, TestCategory("Stress"), TestCategory("CorePerf"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_Stress_Read()
        {
            const int numIterations = 100;
            string grainType = typeof(PersistenceProviderErrorGrain).FullName;

            List<Task<int>> promises = new List<Task<int>>();
            for (int i = 0; i < numIterations; i++)
            {
                int expectedVal = i;
                var grain = PersistenceProviderErrorGrainFactory.GetGrain(i);
                Guid guid = grain.GetPrimaryKey();
                string id = guid.ToString("N");
                SetStoredValue(ErrorInjectorStorageProvider, grainType, grain, "Field1", expectedVal); // Update state data behind grain
                promises.Add(grain.DoRead());
            }
            await Task.WhenAll(promises);

            for (int i = 0; i < numIterations; i++)
            {
                int expectedVal = i;
                Assert.AreEqual(expectedVal, promises[i].Result, "Returned value - Read @ #" + i);
            }

            SetErrorInjection(ErrorInjectorStorageProvider, ErrorInjectionPoint.AfterRead); // Start the error condition
            promises.Clear();

            for (int i = 0; i < numIterations; i++)
            {
                var grain = PersistenceProviderErrorGrainFactory.GetGrain(i);
                promises.Add(grain.DoRead());
            }
            foreach (var p in promises)
            {
                var promise = p; // Capture
                CheckStorageProviderErrors(() => promise);
            }

            SetErrorInjection(ErrorInjectorStorageProvider, ErrorInjectionPoint.None); // End the error condition
            promises.Clear();

            for (int i = 0; i < numIterations; i++)
            {
                var grain = PersistenceProviderErrorGrainFactory.GetGrain(i);
                promises.Add(grain.GetValue());
            }
            await Task.WhenAll(promises);
            for (int i = 0; i < numIterations; i++)
            {
                int expectedVal = i;
                Assert.AreEqual(expectedVal, promises[i].Result, "Returned value - ReRead @ #" + i);
            }
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Grain_BadProvider()
        {
            try
            {
                Guid id = Guid.NewGuid();
                IBadProviderTestGrain grain = BadProviderTestGrainFactory.GetGrain(id);

                await grain.DoSomething();

                string msg = "BadProviderConfigException exception should have been thrown";
                Console.WriteLine("Test failed: {0}", msg);
                Assert.Fail(msg);
            }

            catch (Exception e)
            {
                Console.WriteLine("Exception caught: {0}", e);
                var exc = e.GetBaseException();
                while (exc is OrleansException && exc.InnerException != null)
                {
                    exc = exc.InnerException;
                }
                Console.WriteLine("Checking exception type: {0} Inner type: {1} Details: {2}",
                    exc.GetType().FullName, exc.InnerException != null ? exc.InnerException.GetType().FullName : "Null", exc);
                Assert.IsTrue(exc.Message.Contains(typeof(BadProviderConfigException).Name), "Expected BadProviderConfigException, Got: " + exc);
                // TODO: Currently can't work out why this doesn't work
                //Assert.IsInstanceOfType(exc, typeof(BadProviderConfigException), "Expected type should be BadProviderConfigException");
            }
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence")]
        public void OrleansException_BadProvider()
        {
            string msg1 = "BadProvider";
            string msg2 = "Wrapper";
            string msg3 = "Aggregate";

            var bpce = new BadProviderConfigException(msg1);
            var oe = new OrleansException(msg2, bpce);
            var ae = new AggregateException(msg3, oe);

            Assert.IsNotNull(ae.InnerException, "AggregateException.InnerException should not be null");
            Assert.IsInstanceOfType(ae.InnerException, typeof(OrleansException));
            Exception exc = ae.InnerException;
            Assert.IsNotNull(exc.InnerException, "OrleansException.InnerException should not be null");
            Assert.IsInstanceOfType(exc.InnerException, typeof(BadProviderConfigException));

            exc = ae.GetBaseException();
            Assert.IsNotNull(exc.InnerException, "BaseException.InnerException should not be null");
            Assert.IsInstanceOfType(exc.InnerException, typeof(BadProviderConfigException));

            Assert.AreEqual(msg3, ae.Message, "AggregateException.Message should be '{0}'", msg3);
            Assert.AreEqual(msg2, exc.Message, "OrleansException.Message should be '{0}'", msg2);
            Assert.AreEqual(msg1, exc.InnerException.Message, "InnerException.Message should be '{0}'", msg1);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public async Task MemoryStore_UserGrain_Read_Write()
        {
            long id = 0;
            IUser grain = UserFactory.GetGrain(id);

            string name = id.ToString(CultureInfo.InvariantCulture);

            await grain.SetName(name);

            string readName = await grain.GetName();

            Assert.AreEqual(name, readName, "Read back previously set name");

            long id1 = 1;
            long id2 = 2;
            string name1 = id1.ToString(CultureInfo.InvariantCulture);
            string name2 = id2.ToString(CultureInfo.InvariantCulture);
            IUser friend1 = UserFactory.GetGrain(id1);
            IUser friend2 = UserFactory.GetGrain(id2);
            await friend1.SetName(name1);
            await friend2.SetName(name2);

            var readName1 = await friend1.GetName();
            var readName2 = await friend2.GetName();

            Assert.AreEqual(name1, readName1, "Friend #1 Name");
            Assert.AreEqual(name2, readName2, "Friend #2 Name");

            await grain.AddFriend(friend1);
            await grain.AddFriend(friend2);

            var friends = await grain.GetFriends();
            Assert.AreEqual(2, friends.Count, "Number of friends");
            Assert.AreEqual(name1, await friends[0].GetName(), "GetFriends - Friend #1 Name");
            Assert.AreEqual(name2, await friends[1].GetName(), "GetFriends - Friend #2 Name");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence")]
        public async Task Persistence_Grain_NoState()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceNoStateTestGrain grain = PersistenceNoStateTestGrainFactory.GetGrain(id);

            await grain.DoSomething();

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName, true);

            Assert.AreEqual(null, storageProvider, "StorageProvider found");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Serialization")]
        public async Task Grain_Serialize_Func()
        {
            Guid id = Guid.NewGuid();
            ISerializationTestGrain grain = SerializationTestGrainFactory.GetGrain(id);
            await grain.Test_Serialize_Func();
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Serialization")]
        public async Task Grain_Serialize_Predicate()
        {
            Guid id = Guid.NewGuid();
            ISerializationTestGrain grain = SerializationTestGrainFactory.GetGrain(id);
            await grain.Test_Serialize_Predicate();
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Serialization")]
        public async Task Grain_Serialize_Predicate_Class()
        {
            Guid id = Guid.NewGuid();
            ISerializationTestGrain grain = SerializationTestGrainFactory.GetGrain(id);
            await grain.Test_Serialize_Predicate_Class();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Serialization")]
        public async Task Grain_Serialize_Predicate_Class_Param()
        {
            Guid id = Guid.NewGuid();
            ISerializationTestGrain grain = SerializationTestGrainFactory.GetGrain(id);

            IMyPredicate pred = new MyPredicate(42);
            await grain.Test_Serialize_Predicate_Class_Param(pred);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Serialization")]
        public void Serialize_GrainState_DeepCopy()
        {
            // NOTE: This test requires Silo to be running & Client init so that grain references can be resolved before serialization.
            IUser[] grains = new IUser[3];
            grains[0] = UserFactory.GetGrain(0);
            grains[1] = UserFactory.GetGrain(1);
            grains[2] = UserFactory.GetGrain(2);

            GrainStateContainingGrainReferences initialState = new GrainStateContainingGrainReferences();
            foreach (var g in grains)
            {
                initialState.GrainList.Add(g);
                initialState.GrainDict.Add(g.GetPrimaryKeyLong().ToString(CultureInfo.InvariantCulture), g);
            }

            GrainStateContainingGrainReferences copy = (GrainStateContainingGrainReferences)initialState.DeepCopy();
            Assert.AreNotSame(initialState.GrainDict, copy.GrainDict, "Dictionary");
            Assert.AreNotSame(initialState.GrainList, copy.GrainList, "List");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Serialization"), TestCategory("CorePerf")]
        public async Task Serialize_GrainState_DeepCopy_Stress()
        {
            int num = 100;
            int loops = num * 1000;
            GrainStateContainingGrainReferences[] states = new GrainStateContainingGrainReferences[num];
            for (int i = 0; i < num; i++)
            {
                IUser grain = UserFactory.GetGrain(i);
                states[i] = new GrainStateContainingGrainReferences();
                states[i].GrainList.Add(grain);
                states[i].GrainDict.Add(grain.GetPrimaryKeyLong().ToString(CultureInfo.InvariantCulture), grain);
            }

            List<Task> tasks = new List<Task>();
            for (int i = 0; i < loops; i++)
            {
                int idx = random.Next(num);
                tasks.Add(Task.Run(() => { var copy = states[idx].DeepCopy(); }));
                tasks.Add(Task.Run(() => { var other = SerializationManager.RoundTripSerializationForTesting(states[idx]); }));
            }
            await Task.WhenAll(tasks);

            //Task copyTask = Task.Run(() =>
            //{
            //    for (int i = 0; i < loops; i++)
            //    {
            //        int idx = random.Next(num);
            //        var copy = states[idx].DeepCopy();
            //    }
            //});
            //Task serializeTask = Task.Run(() =>
            //{
            //    for (int i = 0; i < loops; i++)
            //    {
            //        int idx = random.Next(num);
            //        var other = SerializationManager.RoundTripSerializationForTesting(states[idx]);
            //    }
            //});
            //await Task.WhenAll(copyTask, serializeTask);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Scheduler"), TestCategory("Reentrancy")]
        public async Task ReentrentGrainWithState()
        {
            Guid id1 = Guid.NewGuid();
            Guid id2 = Guid.NewGuid();
            IReentrentGrainWithState grain1 = ReentrentGrainWithStateFactory.GetGrain(id1);
            IReentrentGrainWithState grain2 = ReentrentGrainWithStateFactory.GetGrain(id2);
            await Task.WhenAll(grain1.Setup(grain2), grain2.Setup(grain1));

            Task t11 = grain1.Test1();
            Task t12 = grain1.Test2();
            Task t21 = grain2.Test1();
            Task t22 = grain2.Test2();
            await Task.WhenAll(t11, t12, t21, t22);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Scheduler"), TestCategory("Reentrancy")]
        public async Task NonReentrentStressGrainWithoutState()
        {
            Guid id1 = Guid.NewGuid();
            INonReentrentStressGrainWithoutState grain1 = NonReentrentStressGrainWithoutStateFactory.GetGrain(id1);
            await grain1.Test1();
        }

        private const bool DoStart = true; // Task.Delay tests fail (Timeout) unless True

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Scheduler"), TestCategory("Reentrancy")]
        public async Task ReentrentGrain_Task_Delay()
        {
            Guid id1 = Guid.NewGuid();
            IReentrentGrainWithState grain1 = ReentrentGrainWithStateFactory.GetGrain(id1);

            await grain1.Task_Delay(DoStart);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Scheduler"), TestCategory("Reentrancy")]
        public async Task NonReentrentGrain_Task_Delay()
        {
            Guid id1 = Guid.NewGuid();
            INonReentrentStressGrainWithoutState grain1 = NonReentrentStressGrainWithoutStateFactory.GetGrain(id1);

            await grain1.Task_Delay(DoStart);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Scheduler"), TestCategory("Reentrancy")]
        public async Task StateInheritanceTest()
        {
            Guid id1 = Guid.NewGuid();
            IStateInheritanceTestGrain grain = StateInheritanceTestGrainFactory.GetGrain(id1);

            await grain.SetValue(1);
            int val = await grain.GetValue();
            Assert.AreEqual(1, val);
        }

        #region Utility functions
        // ---------- Utility functions ----------

        private void SetStoredValue(string providerName, string grainType, IGrain grain, string fieldName, int newValue)
        {
            List<SiloHandle> silos = GetActiveSilos().ToList();
            foreach (var siloHandle in silos)
            {
                MockStorageProvider provider = (MockStorageProvider)siloHandle.Silo.TestHookup.GetStorageProvider(providerName);
                provider.SetValue(grainType, grain.AsReference(), fieldName, newValue);
            }
        }

        private void SetErrorInjection(string providerName, ErrorInjectionPoint errorInjectionPoint)
        {
            List<SiloHandle> silos = GetActiveSilos().ToList();
            foreach (var siloHandle in silos)
            {
                ErrorInjectionStorageProvider provider = (ErrorInjectionStorageProvider)siloHandle.Silo.TestHookup.GetStorageProvider(providerName);
                provider.SetErrorInjection(errorInjectionPoint);
            }
        }

        private static void CheckStorageProviderErrors(
            Func<Task> taskFunc)
        {
            StackTrace at = new StackTrace();
            TimeSpan timeout = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(15);
            try
            {
                bool ok = taskFunc().Wait(timeout);
                if (!ok) throw new TimeoutException();

                if (ErrorInjectionStorageProvider.DoInjectErrors)
                {
                    string msg = "StorageProviderInjectedError exception should have been thrown " + at;
                    Console.WriteLine("Assertion failed: {0}", msg);
                    Assert.Fail(msg);
                }
            }
            catch (AssertFailedException)
            {
                throw;
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception caught: {0}", e);
                var exc = e.GetBaseException();
                if (exc is OrleansException)
                {
                    exc = exc.InnerException;
                }
                Assert.IsInstanceOfType(exc, typeof(StorageProviderInjectedError));
                //if (exc is StorageProviderInjectedError)
                //{
                //     //Expected error
                //}
                //else
                //{
                //    Console.WriteLine("Unexpected exception: {0}", exc);
                //    Assert.Fail(exc.ToString());
                //}
            }
        }

        private MockStorageProvider FindStorageProviderInUse(string providerName, bool okNull = false)
        {
            MockStorageProvider providerInUse = null;
            SiloHandle siloInUse = null;
            List<SiloHandle> silos = GetActiveSilos().ToList();
            foreach (var siloHandle in silos)
            {
                MockStorageProvider provider = (MockStorageProvider)siloHandle.Silo.TestHookup.GetStorageProvider(providerName);
                Assert.IsNotNull(provider, "No storage provider found: Name={0} Silo={1}", providerName, siloHandle.Silo.SiloAddress);
                if (provider.ReadCount > 0)
                {
                    if (providerInUse != null)
                    {
                        Assert.Fail("Found multiple storage provider in use: Name={0} Silos= {1} {2}",
                            providerName, siloHandle.Silo.SiloAddress, siloInUse.Silo.SiloAddress);
                    }
                    providerInUse = provider;
                    siloInUse = siloHandle;
                }
            }
            if (!okNull && providerInUse == null)
            {
                Assert.Fail("Cannot find active storage provider currently in use, Name={0}", providerName);
            }
            return providerInUse;
        }
        #endregion
    }

    /// <summary>
    /// PersistenceGrainTests using AzureStore - Requires access to external Azure storage
    /// </summary>
    [TestClass]
    [DeploymentItem("Config_AzureTableStorage.xml")]
    public class PersistenceGrainTests_AzureStore : UnitTestBase
    {
        private readonly double timingFactor;

        private const int LoopIterations_Grain = 1000;
        private const int BatchSize = 100;

        private const int MaxReadTime = 200;
        private const int MaxWriteTime = 2000;

        private static readonly Options testSiloOptions = new Options
        {
            SiloConfigFile = new FileInfo("Config_AzureTableStorage.xml"),
            StartFreshOrleans = true,
            StartPrimary = true,
            StartSecondary = false,
        };

        // Use ClassCleanup to run code after all tests in a class have run
        [ClassCleanup]
        public static void ClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        // Use ClassInitialize to run code before running the first test in the class
        // [ClassInitialize()]
        // public static void MyClassInitialize(TestContext testContext) { }
        //
        // Use ClassCleanup to run code after all tests in a class have run
        // [ClassCleanup()]
        // public static void MyClassCleanup() { }
        //
        // Use TestInitialize to run code before running each test 
        // [TestInitialize()]
        // public void MyTestInitialize() { }
        //
        // Use TestCleanup to run code after each test has run
        // [TestCleanup()]
        // public void MyTestCleanup() { }

        public PersistenceGrainTests_AzureStore()
            : base(testSiloOptions)
        {
            timingFactor = CalibrateTimings();
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_AzureStore_Delete()
        {
            Guid id = Guid.NewGuid();
            IAzureStorageTestGrain grain = AzureStorageTestGrainFactory.GetGrain(id);

            await grain.DoWrite(1);

            await grain.DoDelete();

            int val = await grain.GetValue(); // Should this throw instead?
            Assert.AreEqual(0, val, "Value after Delete");

            await grain.DoWrite(2);

            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Delete + New Write");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_AzureStore_Read()
        {
            Guid id = Guid.NewGuid();
            IAzureStorageTestGrain grain = AzureStorageTestGrainFactory.GetGrain(id);

            int val = await grain.GetValue();

            Assert.AreEqual(0, val, "Initial value");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_GuidKey_AzureStore_Read_Write()
        {
            Guid id = Guid.NewGuid();
            IAzureStorageTestGrain grain = AzureStorageTestGrainFactory.GetGrain(id);

            int val = await grain.GetValue();

            Assert.AreEqual(0, val, "Initial value");

            await grain.DoWrite(1);
            val = await grain.GetValue();
            Assert.AreEqual(1, val, "Value after Write-1");

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Write-2");

            val = await grain.DoRead();

            Assert.AreEqual(2, val, "Value after Re-Read");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_LongKey_AzureStore_Read_Write()
        {
            long id = random.Next();
            IAzureStorageTestGrain_LongKey grain = AzureStorageTestGrain_LongKeyFactory.GetGrain(id);

            int val = await grain.GetValue();

            Assert.AreEqual(0, val, "Initial value");

            await grain.DoWrite(1);
            val = await grain.GetValue();
            Assert.AreEqual(1, val, "Value after Write-1");

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Write-2");

            val = await grain.DoRead();

            Assert.AreEqual(2, val, "Value after Re-Read");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_LongKeyExtended_AzureStore_Read_Write()
        {
            long id = random.Next();
            string extKey = random.Next().ToString(CultureInfo.InvariantCulture);

            IAzureStorageTestGrain_LongExtendedKey
                grain = AzureStorageTestGrain_LongExtendedKeyFactory.GetGrain(id, extKey);

            int val = await grain.GetValue();

            Assert.AreEqual(0, val, "Initial value");

            await grain.DoWrite(1);
            val = await grain.GetValue();
            Assert.AreEqual(1, val, "Value after Write-1");

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Write-2");

            val = await grain.DoRead();
            Assert.AreEqual(2, val, "Value after DoRead");

            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Re-Read");

            string extKeyValue = await grain.GetExtendedKeyValue();
            Assert.AreEqual(extKey, extKeyValue, "Extended Key");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_GuidKeyExtended_AzureStore_Read_Write()
        {
            long id = random.Next();
            string extKey = random.Next().ToString(CultureInfo.InvariantCulture);

            IAzureStorageTestGrain_GuidExtendedKey
                grain = AzureStorageTestGrain_GuidExtendedKeyFactory.GetGrain(id, extKey);

            int val = await grain.GetValue();

            Assert.AreEqual(0, val, "Initial value");

            await grain.DoWrite(1);
            val = await grain.GetValue();
            Assert.AreEqual(1, val, "Value after Write-1");

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Write-2");

            val = await grain.DoRead();
            Assert.AreEqual(2, val, "Value after DoRead");

            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Re-Read");

            string extKeyValue = await grain.GetExtendedKeyValue();
            Assert.AreEqual(extKey, extKeyValue, "Extended Key");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_Generic_AzureStore_Read_Write()
        {
            long id = random.Next();

            IAzureStorageGenericGrain<int> grain = AzureStorageGenericGrainFactory<int>.GetGrain(id);

            int val = await grain.GetValue();

            Assert.AreEqual(0, val, "Initial value");

            await grain.DoWrite(1);
            val = await grain.GetValue();
            Assert.AreEqual(1, val, "Value after Write-1");

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Write-2");

            val = await grain.DoRead();

            Assert.AreEqual(2, val, "Value after Re-Read");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_Generic_AzureStore_DiffTypes()
        {
            long id1 = random.Next();
            long id2 = id1;
            long id3 = id1;

            IAzureStorageGenericGrain<int> grain1 = AzureStorageGenericGrainFactory<int>.GetGrain(id1);

            IAzureStorageGenericGrain<string> grain2 = AzureStorageGenericGrainFactory<string>.GetGrain(id2);

            IAzureStorageGenericGrain<double> grain3 = AzureStorageGenericGrainFactory<double>.GetGrain(id3);

            int val1 = await grain1.GetValue();
            Assert.AreEqual(0, val1, "Initial value - 1");

            string val2 = await grain2.GetValue();
            Assert.AreEqual(null, val2, "Initial value - 2");

            double val3 = await grain3.GetValue();
            Assert.AreEqual(0.0, val3, "Initial value - 3");

            int expected1 = 1;
            await grain1.DoWrite(expected1);
            val1 = await grain1.GetValue();
            Assert.AreEqual(expected1, val1, "Value after Write#1 - 1");

            string expected2 = "Three";
            await grain2.DoWrite(expected2);
            val2 = await grain2.GetValue();
            Assert.AreEqual(expected2, val2, "Value after Write#1 - 2");

            double expected3 = 5.1;
            await grain3.DoWrite(expected3);
            val3 = await grain3.GetValue();
            Assert.AreEqual(expected3, val3, "Value after Write#1 - 3");

            val1 = await grain1.GetValue();
            Assert.AreEqual(expected1, val1, "Value before Write#2 - 1");
            expected1 = 2;
            await grain1.DoWrite(expected1);
            val1 = await grain1.GetValue();
            Assert.AreEqual(expected1, val1, "Value after Write#2 - 1");
            val1 = await grain1.DoRead();
            Assert.AreEqual(expected1, val1, "Value after Re-Read - 1");

            val2 = await grain2.GetValue();
            Assert.AreEqual(expected2, val2, "Value before Write#2 - 2");
            expected2 = "Four";
            await grain2.DoWrite(expected2);
            val2 = await grain2.GetValue();
            Assert.AreEqual(expected2, val2, "Value after Write#2 - 2");
            val2 = await grain2.DoRead();
            Assert.AreEqual(expected2, val2, "Value after Re-Read - 2");

            val3 = await grain3.GetValue();
            Assert.AreEqual(expected3, val3, "Value before Write#2 - 3");
            expected3 = 6.2;
            await grain3.DoWrite(expected3);
            val3 = await grain3.GetValue();
            Assert.AreEqual(expected3, val3, "Value after Write#2 - 3");
            val3 = await grain3.DoRead();
            Assert.AreEqual(expected3, val3, "Value after Re-Read - 3");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_AzureStore_SiloRestart()
        {
            var initialDeploymentId = DeploymentId;
            var initialServiceId = ServiceId;
            Console.WriteLine("DeploymentId={0} ServiceId={1}", DeploymentId, ServiceId);

            Guid id = Guid.NewGuid();
            IAzureStorageTestGrain grain = AzureStorageTestGrainFactory.GetGrain(id);

            int val = await grain.GetValue();

            Assert.AreEqual(0, val, "Initial value");

            await grain.DoWrite(1);

            Console.WriteLine("About to reset Silos");
            ResetDefaultRuntimes();
            Initialize(testSiloOptions);
            Console.WriteLine("Silos restarted");

            Console.WriteLine("DeploymentId={0} ServiceId={1}", DeploymentId, ServiceId);
            Assert.AreEqual(initialServiceId, ServiceId, "ServiceId same after restart.");
            Assert.AreNotEqual(initialDeploymentId, DeploymentId, "DeploymentId different after restart.");

            val = await grain.GetValue();
            Assert.AreEqual(1, val, "Value after Write-1");

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Write-2");

            val = await grain.DoRead();

            Assert.AreEqual(2, val, "Value after Re-Read");
        }

        [TestMethod, TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("Performance"), TestCategory("Azure"), TestCategory("Stress")]
        public void Persistence_Perf_Activate()
        {
            const string testName = "Persistence_Perf_Activate";
            int n = LoopIterations_Grain;
            TimeSpan target = TimeSpan.FromMilliseconds(MaxReadTime * n);

            // Timings for Activate
            RunPerfTest(n, testName, target,
                grainNoState => grainNoState.PingAsync(),
                grainMemory => grainMemory.DoSomething(),
                grainMemoryStore => grainMemoryStore.GetValue(),
                grainAzureTable => grainAzureTable.GetValue());
        }

        [TestMethod, TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("Performance"), TestCategory("Azure"), TestCategory("Stress")]
        public void Persistence_Perf_Write()
        {
            const string testName = "Persistence_Perf_Write";
            int n = LoopIterations_Grain;
            TimeSpan target = TimeSpan.FromMilliseconds(MaxWriteTime * n);

            // Timings for Write
            RunPerfTest(n, testName, target,
                grainNoState => grainNoState.EchoAsync(testName),
                grainMemory => grainMemory.DoWrite(n),
                grainMemoryStore => grainMemoryStore.DoWrite(n),
                grainAzureTable => grainAzureTable.DoWrite(n));
        }

        [TestMethod, TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("Performance"), TestCategory("Azure"), TestCategory("Stress")]
        public void Persistence_Perf_Write_Reread()
        {
            const string testName = "Persistence_Perf_Write_Read";
            int n = LoopIterations_Grain;
            TimeSpan target = TimeSpan.FromMilliseconds(MaxWriteTime * n);

            // Timings for Write
            RunPerfTest(n, testName + "--Write", target,
                grainNoState => grainNoState.EchoAsync(testName),
                grainMemory => grainMemory.DoWrite(n),
                grainMemoryStore => grainMemoryStore.DoWrite(n),
                grainAzureTable => grainAzureTable.DoWrite(n));

            // Timings for Activate
            RunPerfTest(n, testName + "--ReRead", target,
                grainNoState => grainNoState.GetLastEchoAsync(),
                grainMemory => grainMemory.DoRead(),
                grainMemoryStore => grainMemoryStore.DoRead(),
                grainAzureTable => grainAzureTable.DoRead());
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Azure")]
        public void AzureStore_ConvertToFromStorageFormat_GrainReference()
        {
            // NOTE: This test requires Silo to be running & Client init so that grain references can be resolved before serialization.
            Guid id = Guid.NewGuid();
            IUser grain = UserFactory.GetGrain(id);

            var initialState = new GrainStateContainingGrainReferences { Grain = grain };
            var entity = new AzureTableStorage.GrainStateEntity();
            var storage = new AzureTableStorage();
            storage.InitLogger(logger);
            storage.ConvertToStorageFormat(initialState, entity);
            Assert.IsNotNull(entity.Data, "Entity.Data");
            var convertedState = new GrainStateContainingGrainReferences();
            storage.ConvertFromStorageFormat(convertedState, entity);
            Assert.IsNotNull(convertedState, "Converted state");
            Assert.AreEqual(initialState.Grain, convertedState.Grain, "Grain");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Azure")]
        public void AzureStore_ConvertToFromStorageFormat_GrainReference_List()
        {
            // NOTE: This test requires Silo to be running & Client init so that grain references can be resolved before serialization.
            IUser[] grains = new IUser[3];
            grains[0] = UserFactory.GetGrain(0);
            grains[1] = UserFactory.GetGrain(1);
            grains[2] = UserFactory.GetGrain(2);

            var initialState = new GrainStateContainingGrainReferences();
            foreach (var g in grains)
            {
                initialState.GrainList.Add(g);
                initialState.GrainDict.Add(g.GetPrimaryKeyLong().ToString(CultureInfo.InvariantCulture), g);
            }
            var entity = new AzureTableStorage.GrainStateEntity();
            var storage = new AzureTableStorage();
            storage.InitLogger(logger);
            storage.ConvertToStorageFormat(initialState, entity);
            Assert.IsNotNull(entity.Data, "Entity.Data");
            var convertedState = new GrainStateContainingGrainReferences();
            storage.ConvertFromStorageFormat(convertedState, entity);
            Assert.IsNotNull(convertedState, "Converted state");
            Assert.AreEqual(initialState.GrainList.Count, convertedState.GrainList.Count, "GrainList size");
            Assert.AreEqual(initialState.GrainDict.Count, convertedState.GrainDict.Count, "GrainDict size");
            for (int i = 0; i < grains.Length; i++)
            {
                string iStr = i.ToString(CultureInfo.InvariantCulture);
                Assert.AreEqual(initialState.GrainList[i], convertedState.GrainList[i], "GrainList #{0}", i);
                Assert.AreEqual(initialState.GrainDict[iStr], convertedState.GrainDict[iStr], "GrainDict #{0}", i);
            }
            Assert.AreEqual(initialState.Grain, convertedState.Grain, "Grain");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Azure")]
        public void Persistence_Silo_StorageProvider_Azure()
        {
            List<SiloHandle> silos = GetActiveSilos().ToList();
            foreach (var silo in silos)
            {
                string provider = typeof(AzureTableStorage).FullName;
                List<string> providers = silo.Silo.TestHookup.GetStorageProviderNames().ToList();
                Assert.IsTrue(providers.Contains(provider), "No storage provider found: {0}", provider);
            }
        }

        #region Utility functions
        // ---------- Utility functions ----------

        private void RunPerfTest(int n, string testName, TimeSpan target,
            Func<IEchoTaskGrain, Task> actionNoState,
            Func<IPersistenceTestGrain, Task> actionMemory,
            Func<IMemoryStorageTestGrain, Task> actionMemoryStore,
            Func<IAzureStorageTestGrain, Task> actionAzureTable)
        {
            IEchoTaskGrain[] noStateGrains = new IEchoTaskGrain[n];
            IPersistenceTestGrain[] memoryGrains = new IPersistenceTestGrain[n];
            IAzureStorageTestGrain[] azureStoreGrains = new IAzureStorageTestGrain[n];
            IMemoryStorageTestGrain[] memoryStoreGrains = new IMemoryStorageTestGrain[n];

            for (int i = 0; i < n; i++)
            {
                int id = i;
                noStateGrains[i] = EchoTaskGrainFactory.GetGrain(id);
                memoryGrains[i] = PersistenceTestGrainFactory.GetGrain(id);
                azureStoreGrains[i] = AzureStorageTestGrainFactory.GetGrain(id);
                memoryStoreGrains[i] = MemoryStorageTestGrainFactory.GetGrain(id);
            }

            TimeSpan baseline, elapsed;

            elapsed = baseline = TimeRun(n, TimeSpan.Zero, testName + " (No state)",
                () => RunIterations(testName, n, i => actionNoState(noStateGrains[i])));

            elapsed = TimeRun(n, baseline, testName + " (Local Memory Store)",
                () => RunIterations(testName, n, i => actionMemory(memoryGrains[i])));

            elapsed = TimeRun(n, baseline, testName + " (Dev Store Grain Store)",
                () => RunIterations(testName, n, i => actionMemoryStore(memoryStoreGrains[i])));

            elapsed = TimeRun(n, baseline, testName + " (Azure Table Store)",
                () => RunIterations(testName, n, i => actionAzureTable(azureStoreGrains[i])));

            if (elapsed > target.Multiply(timingFactor))
            {
                string msg = string.Format("{0}: Elapsed time {1} exceeds target time {2}", testName, elapsed, target);

                if (elapsed > target.Multiply(2.0 * timingFactor))
                {
                    Assert.Fail(msg);
                }
                else
                {
                    Assert.Inconclusive(msg);
                }
            }
        }

        private static void RunIterations(string testName, int n, Func<int, Task> action)
        {
            List<Task> promises = new List<Task>();
            Stopwatch sw = Stopwatch.StartNew();
            // Fire off requests in batches
            for (int i = 0; i < n; i++)
            {
                var promise = action(i);
                promises.Add(promise);
                if ((i % BatchSize) == 0 && i > 0)
                {
                    Task.WaitAll(promises.ToArray(), AzureTableDefaultPolicies.TableCreationTimeout);
                    promises.Clear();
                    //Console.WriteLine("{0} has done {1} iterations  in {2} at {3} RPS",
                    //                  testName, i, sw.Elapsed, i / sw.Elapsed.TotalSeconds);
                }
            }
            Task.WaitAll(promises.ToArray(), AzureTableDefaultPolicies.TableCreationTimeout);
            sw.Stop();
            Console.WriteLine("{0} completed. Did {1} iterations in {2} at {3} RPS",
                              testName, n, sw.Elapsed, n / sw.Elapsed.TotalSeconds);
        }
        #endregion
    }

    public class GrainStateContainingGrainReferences : GrainState
    {
        public IAddressable Grain { get; set; }
        public List<IAddressable> GrainList { get; set; }
        public Dictionary<string, IAddressable> GrainDict { get; set; }

        public GrainStateContainingGrainReferences()
            : base("System.Object")
        {
            GrainList = new List<IAddressable>();
            GrainDict = new Dictionary<string, IAddressable>();
        }

        public override IDictionary<string, object> AsDictionary()
        {
            Dictionary<string, object> values = new Dictionary<string, object>();
            values["Grain"] = this.Grain;
            values["GrainList"] = this.GrainList;
            values["GrainDict"] = this.GrainDict;
            return values;
        }

        public override void SetAll(IDictionary<string, object> values)
        {
            object value;
            if (values.TryGetValue("Grain", out value)) Grain = (value as IAddressable);
            if (values.TryGetValue("GrainList", out value)) GrainList = (value as List<IAddressable>);
            if (values.TryGetValue("GrainDict", out value)) GrainDict = (value as Dictionary<string, IAddressable>);
        }

        [CopierMethodAttribute]
        public static object _Copier(object original)
        {
            GrainState input = ((GrainState)(original));
            return input.DeepCopy();
        }

        [SerializerMethodAttribute]
        public static void _Serializer(object original, BinaryTokenStreamWriter stream, Type expected)
        {
            GrainState input = ((GrainState)(original));
            input.SerializeTo(stream);
        }

        [DeserializerMethodAttribute]
        public static object _Deserializer(Type expected, BinaryTokenStreamReader stream)
        {
            GrainState result = new GrainStateContainingGrainReferences();
            result.DeserializeFrom(stream);
            return result;
        }
    }
}

// ReSharper restore RedundantAssignment
// ReSharper restore UnusedVariable
// ReSharper restore InconsistentNaming
