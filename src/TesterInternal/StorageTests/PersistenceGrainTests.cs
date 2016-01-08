//#define REREAD_STATE_AFTER_WRITE_FAILED

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;

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
    public class PersistenceGrainTests_Local : UnitTestSiloHost
    {
        const string ErrorInjectorStorageProvider = "ErrorInjector";

        private static readonly TestingSiloOptions testSiloOptions = new TestingSiloOptions
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
            //ResetDefaultRuntimes();
            StopAllSilos();
            AppDomain.CurrentDomain.UnhandledException -= ReportUnhandledException;
            TaskScheduler.UnobservedTaskException -= ReportUnobservedTaskException;
        }

        [TestInitialize]
        public void TestInitialize()
        {
            SerializationManager.InitializeForTesting();
            SetErrorInjection(ErrorInjectorStorageProvider, ErrorInjectionPoint.None);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            SetErrorInjection(ErrorInjectorStorageProvider, ErrorInjectionPoint.None);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public void Persistence_Silo_StorageProviders()
        {
            List<SiloHandle> silos = GetActiveSilos().ToList();
            foreach (var silo in silos)
            {
                List<string> providers = silo.Silo.TestHook.GetStorageProviderNames().ToList();
                Assert.IsNotNull(providers, "Null provider manager");
                Assert.IsTrue(providers.Count > 0, "Some providers loaded");
                const string providerName = "test1";
                IStorageProvider store = silo.Silo.TestHook.GetStorageProvider(providerName);
                Assert.IsNotNull(store, "No storage provider found: {0}", providerName);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public void Persistence_Silo_StorageProvider_Name_LowerCase()
        {
            List<SiloHandle> silos = GetActiveSilos().ToList();
            foreach (var silo in silos)
            {
                const string providerName = "LowerCase";
                IStorageProvider store = silo.Silo.TestHook.GetStorageProvider(providerName);
                Assert.IsNotNull(store, "No storage provider found: {0}", providerName);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        [ExpectedException(typeof(KeyNotFoundException))]
        public void Persistence_Silo_StorageProvider_Name_Missing()
        {
            List<SiloHandle> silos = GetActiveSilos().ToList();
            var silo = silos.First();
            const string providerName = "NotPresent";
            IStorageProvider store = silo.Silo.TestHook.GetStorageProvider(providerName);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_CheckStateInit()
        {
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceTestGrain>(id);
            bool ok = await grain.CheckStateInit();
            Assert.IsTrue(ok, "CheckStateInit OK");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_CheckStorageProvider()
        {
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceTestGrain>(id);
            string providerType = await grain.CheckProviderType();
            Assert.AreEqual(typeof(MockStorageProvider).FullName, providerType, "StorageProvider provider type");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Init()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceTestGrain>(id);

            await grain.DoSomething();

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.AreEqual(1, storageProvider.InitCount, "StorageProvider #Init");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Activate_StoredValue()
        {
            const string providerName = "test1";
            string grainType = typeof(PersistenceTestGrain).FullName;
            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");

            IPersistenceTestGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceTestGrain>(guid);

            // Store initial value in storage
            int initialValue = 567;
            SetStoredValue<PersistenceTestGrainState>(providerName, grainType, grain, "Field1", initialValue);

            int readValue = await grain.GetValue();
            Assert.AreEqual(initialValue, readValue, "Read previously stored value");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Activate_Error()
        {
            const string providerName = ErrorInjectorStorageProvider;
            string grainType = typeof(PersistenceProviderErrorGrain).FullName;
            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");

            IPersistenceProviderErrorGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(guid);

            // Store initial value in storage
            int initialValue = 567;
            SetStoredValue<PersistenceTestGrainState>(providerName, grainType, grain, "Field1", initialValue);

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
                if (e is Exception)
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

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Read()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceTestGrain>(id);

            await grain.DoSomething();

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(0, storageProvider.WriteCount, "StorageProvider #Writes");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Write()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceTestGrain>(id);

            await grain.DoWrite(1);

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(1, storageProvider.WriteCount, "StorageProvider #Writes");

            Assert.AreEqual(1, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");

            await grain.DoWrite(2);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(2, storageProvider.WriteCount, "StorageProvider #Writes");

            Assert.AreEqual(2, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_ReRead()
        {
            const string providerName = "test1";
            string grainType = typeof(PersistenceTestGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceTestGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceTestGrain>(guid);

            await grain.DoSomething();

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(0, storageProvider.WriteCount, "StorageProvider #Writes");
            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", 42); // Update state data behind grain


            await grain.DoRead();

            Assert.AreEqual(2, storageProvider.ReadCount, "StorageProvider #Reads-2");
            Assert.AreEqual(0, storageProvider.WriteCount, "StorageProvider #Writes-2");

            Assert.AreEqual(42, ((PersistenceTestGrainState)storageProvider.LastState).Field1, "Store-Field1");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public async Task MemoryStore_Read_Write()
        {
            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IMemoryStorageTestGrain grain = GrainClient.GrainFactory.GetGrain<IMemoryStorageTestGrain>(guid);

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
                IMemoryStorageTestGrain grain = GrainClient.GrainFactory.GetGrain<IMemoryStorageTestGrain>(Guid.NewGuid());
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
            //Console.WriteLine("{0} Completed {1} Read-Write operations in {2} at {3} TPS", TestContext.TestName, numIterations, elapsed, tps);

            for (int i = 0; i < numIterations; i++)
            {
                int expectedVal = i;
                Assert.AreEqual(expectedVal, promises[i].Result, "Returned value - Read @ #" + i);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Write()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceTestGrain>(id);

            await grain.DoWrite(1);

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(1, storageProvider.WriteCount, "StorageProvider #Writes");

            Assert.AreEqual(1, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");

            await grain.DoWrite(2);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(2, storageProvider.WriteCount, "StorageProvider #Writes");

            Assert.AreEqual(2, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Delete()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceTestGrain>(id);

            await grain.DoWrite(1);

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(1, storageProvider.WriteCount, "StorageProvider #Writes");

            await grain.DoDelete();

            Assert.AreEqual(1, storageProvider.DeleteCount, "StorageProvider #Deletes");
            Assert.AreEqual(null, storageProvider.LastState, "Store-AfterDelete-Empty");

            int val = await grain.GetValue(); // Returns current in-memory null data without re-read.
            Assert.AreEqual(0, val, "Value after Delete");
            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");

            await grain.DoWrite(2);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(2, storageProvider.WriteCount, "StorageProvider #Writes");

            Assert.AreEqual(2, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Read_Error()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceErrorGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceErrorGrain>(id);

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

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Write_Error()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceErrorGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceErrorGrain>(id);

            await grain.DoWrite(1);

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(1, storageProvider.WriteCount, "StorageProvider #Writes");

            Assert.AreEqual(1, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");

            await grain.DoWrite(2);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(2, storageProvider.WriteCount, "StorageProvider #Writes");

            Assert.AreEqual(2, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");

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

            Assert.AreEqual(2, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");

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

            Assert.AreEqual(4, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_ReRead_Error()
        {
            const string providerName = "test1";
            string grainType = typeof(PersistenceErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceErrorGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceErrorGrain>(guid);

            var val = await grain.GetValue();

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.AreEqual(1, storageProvider.ReadCount, "StorageProvider #Reads");
            Assert.AreEqual(0, storageProvider.WriteCount, "StorageProvider #Writes");

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", 42);

            await grain.DoRead();

            Assert.AreEqual(2, storageProvider.ReadCount, "StorageProvider #Reads-2");
            Assert.AreEqual(0, storageProvider.WriteCount, "StorageProvider #Writes-2");

            Assert.AreEqual(42, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");

            await grain.DoWrite(43);

            Assert.AreEqual(2, storageProvider.ReadCount, "StorageProvider #Reads-2");
            Assert.AreEqual(1, storageProvider.WriteCount, "StorageProvider #Writes-2");

            Assert.AreEqual(43, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");

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

            Assert.AreEqual(43, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");

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

            Assert.AreEqual(43, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_BeforeRead()
        {
            string grainType = typeof(PersistenceProviderErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceProviderErrorGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(guid);

            var val = await grain.GetValue(); // Activate grain
            int expectedVal = 42;

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference) grain, "Field1", expectedVal);

            val = await grain.DoRead();

            Assert.AreEqual(expectedVal, val, "Returned value");

            storageProvider.SetErrorInjection(ErrorInjectionPoint.BeforeRead);
            CheckStorageProviderErrors(grain.DoRead);

            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            val = await grain.GetValue();
            Assert.AreEqual(expectedVal, val, "Returned value");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_AfterRead()
        {
            string grainType = typeof(PersistenceProviderErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceProviderErrorGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(guid);

            var val = await grain.GetValue(); // Activate grain
            int expectedVal = 52;

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", expectedVal);

            val = await grain.DoRead();

            Assert.AreEqual(expectedVal, val, "Returned value");

            storageProvider.SetErrorInjection(ErrorInjectionPoint.AfterRead);
            CheckStorageProviderErrors(grain.DoRead);

            val = await grain.GetValue();
            Assert.AreEqual(expectedVal, val, "Returned value");

            int newVal = 53;
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", newVal);

            val = await grain.GetValue();
            Assert.AreEqual(expectedVal, val, "Returned value");

            await grain.DoRead(); // Force re-read
            expectedVal = newVal;

            val = await grain.GetValue();
            Assert.AreEqual(expectedVal, val, "Returned value");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_BeforeWrite()
        {
            Guid id = Guid.NewGuid();
            IPersistenceProviderErrorGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(id);

            var val = await grain.GetValue();

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);

            int expectedVal = 62;
            await grain.DoWrite(expectedVal);
            Assert.AreEqual(expectedVal, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");

            const int attemptedVal3 = 63;
            storageProvider.SetErrorInjection(ErrorInjectionPoint.BeforeWrite);
            CheckStorageProviderErrors(() => grain.DoWrite(attemptedVal3));

            // Stored value unchanged
            Assert.AreEqual(expectedVal, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");

            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);
            val = await grain.GetValue();
            // Stored value unchanged
            Assert.AreEqual(expectedVal, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");
#if REREAD_STATE_AFTER_WRITE_FAILED
            Assert.AreEqual(expectedVal, val, "Last value written successfully");
#else
            Assert.AreEqual(attemptedVal3, val, "Last value attempted to be written is still in memory");
#endif
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_AfterWrite()
        {
            Guid id = Guid.NewGuid();
            IPersistenceProviderErrorGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(id);

            var val = await grain.GetValue();

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);

            int expectedVal = 82;
            await grain.DoWrite(expectedVal);
            Assert.AreEqual(expectedVal, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");

            const int attemptedVal4 = 83;
            storageProvider.SetErrorInjection(ErrorInjectionPoint.AfterWrite);
            CheckStorageProviderErrors(() => grain.DoWrite(attemptedVal4));

            // Stored value has changed
            expectedVal = attemptedVal4;
            Assert.AreEqual(expectedVal, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);
            val = await grain.GetValue();
            Assert.AreEqual(expectedVal, val, "Returned value");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_BeforeReRead()
        {
            string grainType = typeof(PersistenceProviderErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceProviderErrorGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(guid);

            var val = await grain.GetValue();

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);

            int expectedVal = 72;

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", expectedVal);
            val = await grain.DoRead();
            Assert.AreEqual(expectedVal, val, "Returned value");

            expectedVal = 73;
            await grain.DoWrite(expectedVal);
            Assert.AreEqual(expectedVal, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");

            storageProvider.SetErrorInjection(ErrorInjectionPoint.BeforeRead);
            CheckStorageProviderErrors(grain.DoRead);

            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);
            val = await grain.GetValue();
            Assert.AreEqual(expectedVal, val, "Returned value");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_AfterReRead()
        {
            string grainType = typeof(PersistenceProviderErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceProviderErrorGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(guid);

            var val = await grain.GetValue();

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);

            int expectedVal = 92;

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", expectedVal);
            val = await grain.DoRead();
            Assert.AreEqual(expectedVal, val, "Returned value");

            expectedVal = 93;
            await grain.DoWrite(expectedVal);
            Assert.AreEqual(expectedVal, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");

            expectedVal = 94;

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", expectedVal);
            storageProvider.SetErrorInjection(ErrorInjectionPoint.AfterRead);
            CheckStorageProviderErrors(grain.DoRead);

            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);
            val = await grain.GetValue();
            Assert.AreEqual(expectedVal, val, "Returned value");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Error_Handled_Read()
        {
            string grainType = typeof(PersistenceUserHandledErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceUserHandledErrorGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceUserHandledErrorGrain>(guid);

            var val = await grain.GetValue(); // Activate grain
            int expectedVal = 42;

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", expectedVal);

            val = await grain.DoRead(false);

            Assert.AreEqual(expectedVal, val, "Returned value");

            int newVal = expectedVal + 1;

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", newVal);
            storageProvider.SetErrorInjection(ErrorInjectionPoint.BeforeRead);
            val = await grain.DoRead(true);
            Assert.AreEqual(expectedVal, val, "Returned value");

            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            expectedVal = newVal;

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", newVal);
            val = await grain.DoRead(false);
            Assert.AreEqual(expectedVal, val, "Returned value");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Error_Handled_Write()
        {
            string grainType = typeof(PersistenceUserHandledErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceUserHandledErrorGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceUserHandledErrorGrain>(guid);

            var val = await grain.GetValue(); // Activate grain
            int expectedVal = 42;

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", expectedVal);

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

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Error_NotHandled_Write()
        {
            string grainType = typeof(PersistenceUserHandledErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceUserHandledErrorGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceUserHandledErrorGrain>(guid);

            var val = await grain.GetValue(); // Activate grain
            int expectedVal = 42;

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);
            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", expectedVal);

            val = await grain.DoRead(false);

            Assert.AreEqual(expectedVal, val, "Returned value after read");

            int newVal = expectedVal + 1;
            storageProvider.SetErrorInjection(ErrorInjectionPoint.BeforeWrite);
            CheckStorageProviderErrors(() => grain.DoWrite(newVal, false));

            val = await grain.GetValue();
            // Stored value unchanged
            Assert.AreEqual(expectedVal, storageProvider.GetLastState<PersistenceTestGrainState>().Field1, "Store-Field1");
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
                IPersistenceTestGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceTestGrain>(Guid.NewGuid());
                Guid guid = grain.GetPrimaryKey();
                string id = guid.ToString("N");

                SetStoredValue<PersistenceTestGrainState>("test1", grainType, grain, "Field1", expectedVal); // Update state data behind grain
                promises[i] = grain.DoRead();
            }
            await Task.WhenAll(promises);

            for (int i = 0; i < numIterations; i++)
            {
                int expectedVal = i;
                Assert.AreEqual(expectedVal, promises[i].Result, "Returned value - Read @ #" + i);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_BadProvider()
        {
            try
            {
                Guid id = Guid.NewGuid();
                IBadProviderTestGrain grain = GrainClient.GrainFactory.GetGrain<IBadProviderTestGrain>(id);

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

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
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

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public async Task MemoryStore_UserGrain_Read_Write()
        {
            Guid id = Guid.NewGuid();
            IUser grain = GrainClient.GrainFactory.GetGrain<IUser>(id);

            string name = id.ToString();

            await grain.SetName(name);

            string readName = await grain.GetName();

            Assert.AreEqual(name, readName, "Read back previously set name");

            Guid id1 = Guid.NewGuid();
            Guid id2 = Guid.NewGuid();
            string name1 = id1.ToString();
            string name2 = id2.ToString();
            IUser friend1 = GrainClient.GrainFactory.GetGrain<IUser>(id1);
            IUser friend2 = GrainClient.GrainFactory.GetGrain<IUser>(id2);
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

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_NoState()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceNoStateTestGrain grain = GrainClient.GrainFactory.GetGrain<IPersistenceNoStateTestGrain>(id);

            await grain.DoSomething();

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName, true);

            Assert.AreEqual(null, storageProvider, "StorageProvider found");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Serialization")]
        public async Task Grain_Serialize_Func()
        {
            Guid id = Guid.NewGuid();
            ISerializationTestGrain grain = GrainClient.GrainFactory.GetGrain<ISerializationTestGrain>(id);
            await grain.Test_Serialize_Func();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Serialization")]
        public async Task Grain_Serialize_Predicate()
        {
            Guid id = Guid.NewGuid();
            ISerializationTestGrain grain = GrainClient.GrainFactory.GetGrain<ISerializationTestGrain>(id);
            await grain.Test_Serialize_Predicate();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Serialization")]
        public async Task Grain_Serialize_Predicate_Class()
        {
            Guid id = Guid.NewGuid();
            ISerializationTestGrain grain = GrainClient.GrainFactory.GetGrain<ISerializationTestGrain>(id);
            await grain.Test_Serialize_Predicate_Class();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Serialization")]
        public async Task Grain_Serialize_Predicate_Class_Param()
        {
            Guid id = Guid.NewGuid();
            ISerializationTestGrain grain = GrainClient.GrainFactory.GetGrain<ISerializationTestGrain>(id);

            IMyPredicate pred = new MyPredicate(42);
            await grain.Test_Serialize_Predicate_Class_Param(pred);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Serialization")]
        public void Serialize_GrainState_DeepCopy()
        {
            // NOTE: This test requires Silo to be running & Client init so that grain references can be resolved before serialization.
            IUser[] grains = new IUser[3];
            grains[0] = GrainClient.GrainFactory.GetGrain<IUser>(Guid.NewGuid());
            grains[1] = GrainClient.GrainFactory.GetGrain<IUser>(Guid.NewGuid());
            grains[2] = GrainClient.GrainFactory.GetGrain<IUser>(Guid.NewGuid());

            GrainStateContainingGrainReferences initialState = new GrainStateContainingGrainReferences();
            foreach (var g in grains)
            {
                initialState.GrainList.Add(g);
                initialState.GrainDict.Add(g.GetPrimaryKey().ToString(), g);
            }

            var copy = (GrainStateContainingGrainReferences)SerializationManager.DeepCopy(initialState);
            Assert.AreNotSame(initialState.GrainDict, copy.GrainDict, "Dictionary");
            Assert.AreNotSame(initialState.GrainList, copy.GrainList, "List");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Serialization"), TestCategory("CorePerf")]
        public async Task Serialize_GrainState_DeepCopy_Stress()
        {
            int num = 100;
            int loops = num * 1000;
            GrainStateContainingGrainReferences[] states = new GrainStateContainingGrainReferences[num];
            for (int i = 0; i < num; i++)
            {
                IUser grain = GrainClient.GrainFactory.GetGrain<IUser>(Guid.NewGuid());
                states[i] = new GrainStateContainingGrainReferences();
                states[i].GrainList.Add(grain);
                states[i].GrainDict.Add(grain.GetPrimaryKey().ToString(), grain);
            }

            List<Task> tasks = new List<Task>();
            for (int i = 0; i < loops; i++)
            {
                int idx = random.Next(num);
                tasks.Add(Task.Run(() => { var copy = SerializationManager.DeepCopy(states[idx]); }));
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

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Scheduler"), TestCategory("Reentrancy")]
        public async Task ReentrentGrainWithState()
        {
            Guid id1 = Guid.NewGuid();
            Guid id2 = Guid.NewGuid();
            IReentrentGrainWithState grain1 = GrainClient.GrainFactory.GetGrain<IReentrentGrainWithState>(id1);
            IReentrentGrainWithState grain2 = GrainClient.GrainFactory.GetGrain<IReentrentGrainWithState>(id2);
            await Task.WhenAll(grain1.Setup(grain2), grain2.Setup(grain1));

            Task t11 = grain1.Test1();
            Task t12 = grain1.Test2();
            Task t21 = grain2.Test1();
            Task t22 = grain2.Test2();
            await Task.WhenAll(t11, t12, t21, t22);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Scheduler"), TestCategory("Reentrancy")]
        public async Task NonReentrentStressGrainWithoutState()
        {
            Guid id1 = Guid.NewGuid();
            INonReentrentStressGrainWithoutState grain1 = GrainClient.GrainFactory.GetGrain<INonReentrentStressGrainWithoutState>(id1);
            await grain1.Test1();
        }

        private const bool DoStart = true; // Task.Delay tests fail (Timeout) unless True

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Scheduler"), TestCategory("Reentrancy")]
        public async Task ReentrentGrain_Task_Delay()
        {
            Guid id1 = Guid.NewGuid();
            IReentrentGrainWithState grain1 = GrainClient.GrainFactory.GetGrain<IReentrentGrainWithState>(id1);

            await grain1.Task_Delay(DoStart);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Scheduler"), TestCategory("Reentrancy")]
        public async Task NonReentrentGrain_Task_Delay()
        {
            Guid id1 = Guid.NewGuid();
            INonReentrentStressGrainWithoutState grain1 = GrainClient.GrainFactory.GetGrain<INonReentrentStressGrainWithoutState>(id1);

            await grain1.Task_Delay(DoStart);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Scheduler"), TestCategory("Reentrancy")]
        public async Task StateInheritanceTest()
        {
            Guid id1 = Guid.NewGuid();
            IStateInheritanceTestGrain grain = GrainClient.GrainFactory.GetGrain<IStateInheritanceTestGrain>(id1);

            await grain.SetValue(1);
            int val = await grain.GetValue();
            Assert.AreEqual(1, val);
        }

        #region Utility functions
        // ---------- Utility functions ----------
        private void SetStoredValue<TState>(string providerName, string grainType, IGrain grain, string fieldName, int newValue)
        {
            List<SiloHandle> silos = GetActiveSilos().ToList();
            foreach (var siloHandle in silos)
            {
                MockStorageProvider provider = (MockStorageProvider)siloHandle.Silo.TestHook.GetStorageProvider(providerName);
                provider.SetValue<TState>(grainType, (GrainReference)grain, "Field1", newValue);
            }
        }

        private void SetErrorInjection(string providerName, ErrorInjectionPoint errorInjectionPoint)
        {
            List<SiloHandle> silos = GetActiveSilos().ToList();
            foreach (var siloHandle in silos)
            {
                ErrorInjectionStorageProvider provider = (ErrorInjectionStorageProvider)siloHandle.Silo.TestHook.GetStorageProvider(providerName);
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
                MockStorageProvider provider = (MockStorageProvider)siloHandle.Silo.TestHook.GetStorageProvider(providerName);
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

    [Serializable]
    public class GrainStateContainingGrainReferences
    {
        public IAddressable Grain { get; set; }
        public List<IAddressable> GrainList { get; set; }
        public Dictionary<string, IAddressable> GrainDict { get; set; }

        public GrainStateContainingGrainReferences()
        {
            GrainList = new List<IAddressable>();
            GrainDict = new Dictionary<string, IAddressable>();
        }
    }
}

// ReSharper restore RedundantAssignment
// ReSharper restore UnusedVariable
// ReSharper restore InconsistentNaming
