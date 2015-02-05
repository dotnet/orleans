using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Orleans;
using Orleans.Runtime;

using UnitTestGrainInterfaces;
using UnitTestGrains;

// ReSharper disable ConvertToConstant.Local

namespace UnitTests.StressTests
{
    /// <summary>
    /// Summary description for PersistenceTest
    /// </summary>
    [TestClass]
    public class STMessagingTest : UnitTestBase
    {
        private static readonly Options TestOptions = new Options
        {
            LargeMessageWarningThreshold = 100 * 1000 * 1000,
        };

        public STMessagingTest()
            : base(TestOptions)
        {
            logger.Info("#### STMessagingTest() is called.");
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        [TestMethod, TestCategory("CorePerf"), TestCategory("Performance"), TestCategory("Stress")]
        public async Task STPerformanceTestNonPersistent()
        {
            IErrorGrainWithAsyncMethods nonPersistentGrain = ErrorGrainWithAsyncMethodsFactory.GetGrain(GetRandomGrainId());
            const int nClients = 10;
            const int nIncsPerClient = 10;

            Task[] clients = new Task[nClients];
            for (int i = 0; i < clients.Length; i++)
            {
                clients[i] = Task.Run(() =>
                {
                    for (int j = 0; j < nIncsPerClient; j++)
                    {
                        Task p = nonPersistentGrain.IncrementAAsync_1();
                        Console.Write(j + " ");
                        p.Wait();
                    }
                });
            }
            for (int i = 0; i < clients.Length; i++)
            {
                clients[i].Wait();
            }

            int all = await nonPersistentGrain.GetA();
            Assert.AreEqual(nClients * nIncsPerClient, all);
            Console.WriteLine("\n\nTEST ENDED SUCCESSFULLY\n\n");
        }

        [TestMethod, TestCategory("Failures"), TestCategory("Messaging"), TestCategory("Stress")]
        public async Task STMessagingTest_AlotOfGrains()
        {
            for (int iteration = 0; iteration < 20; iteration++)
            {
                Console.WriteLine("AlotOfGrains::Iteration " + iteration);
                int numGrains = 200;
                List<long> keys = new List<long>();
                List<IStressSelfManagedGrain> grains = new List<IStressSelfManagedGrain>();
                for (int i = 0; i < numGrains; i++)
                {
                    keys.Add(i + 1);
                }

                for (int i = 0; i < keys.Count; i++)
                {
                    long key = keys[i];
                    IStressSelfManagedGrain grain = StressSelfManagedGrainFactory.GetGrain(key);
                    grains.Add(grain);
                    Assert.AreEqual(key, grain.GetPrimaryKeyLong());
                    Assert.AreEqual(key.ToString(CultureInfo.InvariantCulture), await grain.GetLabel());
                    await grain.PingOthers(keys.Except(new long[] { key }).ToArray());
                    //Console.Write(".");
                }
            }
        }

        [TestMethod, TestCategory("Failures"), TestCategory("Messaging"), TestCategory("Stress")]
        public async Task STMessagingTest_LargeMsg()
        {
            for (int iteration = 0; iteration < 20; iteration++)
            {
                Console.WriteLine("LargeMsg::Iteration " + iteration);
                int numGrains = 200;
                int numItems = 5000;
                List<long> keys = new List<long>();
                List<IStressSelfManagedGrain> grains = new List<IStressSelfManagedGrain>();
                for (int i = 0; i < numGrains; i++)
                {
                    keys.Add(i + 1);
                }

                for (int i = 0; i < keys.Count; i++)
                {
                    long key = keys[i];
                    IStressSelfManagedGrain g1 = StressSelfManagedGrainFactory.GetGrain(key);
                    grains.Add(g1);
                    Assert.AreEqual(key, g1.GetPrimaryKeyLong());
                    Assert.AreEqual(key.ToString(CultureInfo.InvariantCulture), await g1.GetLabel());
                    //logger.Info("Grain {0}, activation {1} on {2}", g1.GetGrainId().Result, g1.GetActivationId().Result, g1.GetRuntimeInstanceId().Result);

                    List<Tuple<GrainId, int>> grainAndETagList = new List<Tuple<GrainId, int>>();
                    for (int j = 0; j < numItems; j++)
                    {
                        GrainId id = GrainId.NewId();
                        grainAndETagList.Add(new Tuple<GrainId, int>(id, 7));
                    }
                    List<Task> promises = new List<Task>();
                    for (int k = 0; k < 10; k++)
                    {
                        Task<List<Tuple<GrainId, int, List<Tuple<SiloAddress, ActivationId>>>>> replyPromise = g1.LookUpMany(SiloAddress.NewLocalAddress(1), grainAndETagList);
                        promises.Add(replyPromise);
                    }
                    await Task.WhenAll(promises);
                }
            }
        }

        [TestMethod, TestCategory("Failures"), TestCategory("Messaging"), TestCategory("Stress")]
        public async Task STMessagingTest_LargeMsgBlob_Send()
        {
            const string testName = "LargeMsgBlob_Send";
            const int numGrains = 3;
            TimeSpan testDuration = TimeSpan.FromMinutes(20);

            Stopwatch clock = new Stopwatch();
            clock.Start();

            while (clock.Elapsed < testDuration)
            {
                for (int iteration = 1; iteration <= 100; iteration++)
                {
                    int dataSize = iteration*100*1000;

                    Console.WriteLine("{0}::Iteration #{1} with {2} grains and data size = {3}", testName, iteration, numGrains, dataSize);

                    byte[] data = new byte[dataSize];
                    for (int i = 0; i < dataSize; i++)
                    {
                        data[i] = (byte) (i & 0xFF);
                    }

                    IStressSelfManagedGrain[] grains = new IStressSelfManagedGrain[numGrains];
                    for (int i = 0; i < numGrains; i++)
                    {
                        long key = i + 1;
                        IStressSelfManagedGrain grain = StressSelfManagedGrainFactory.GetGrain(key);
                        Assert.AreEqual(key, grain.GetPrimaryKeyLong());
                        Assert.AreEqual(key.ToString(CultureInfo.InvariantCulture), await grain.GetLabel());
                        grains[i] = grain;
                    }

                    for (int i = 0; i < grains.Length; i++)
                    {
                        long key = i + 1;
                        Console.WriteLine("{0} - Sending {1} byte message to grain key={2}", testName, data.Length, key);
                        var grain = grains[i];
                        await grain.Send(data);
                    }
                }
            }
        }

        [TestMethod, TestCategory("Failures"), TestCategory("Messaging"), TestCategory("Stress")]
        public async Task STMessagingTest_LargeMsgBlob_Echo()
        {
            const string testName = "LargeMsgBlob_Echo";
            const int numGrains = 3;
            TimeSpan testDuration = TimeSpan.FromMinutes(20);

            Stopwatch clock = new Stopwatch();
            clock.Start();

            while (clock.Elapsed < testDuration)
            {

                for (int iteration = 1; iteration <= 10; iteration++)
                {
                    int dataSize = iteration*100*1000;

                    Console.WriteLine("{0}::Iteration #{1} with {2} grains and data size = {3}", testName, iteration,
                                      numGrains, dataSize);

                    byte[] data = new byte[dataSize];
                    for (int i = 0; i < dataSize; i++)
                    {
                        data[i] = (byte) (i & 0xFF);
                    }

                    IStressSelfManagedGrain[] grains = new IStressSelfManagedGrain[numGrains];
                    for (int i = 0; i < numGrains; i++)
                    {
                        long key = i + 1;
                        IStressSelfManagedGrain grain = StressSelfManagedGrainFactory.GetGrain(key);
                        Assert.AreEqual(key, grain.GetPrimaryKeyLong());
                        Assert.AreEqual(key.ToString(CultureInfo.InvariantCulture), await grain.GetLabel());
                        grains[i] = grain;
                    }

                    for (int i = 0; i < grains.Length; i++)
                    {
                        long key = i + 1;
                        Console.WriteLine("{0} - Sending {1} byte message to grain key={2}", testName, data.Length, key);
                        var grain = grains[i];
                        var reply = await grain.Echo(data);

                        Assert.AreEqual(data.Length, reply.Length, "Reply data length");

                        //for (int b = 0; b < data.Length; b++)
                        //{
                        //    Assert.AreEqual(data[b], reply[b], "Reply byte " + b + " value = " + data[b]);
                        //}
                        int b = 0;
                        Assert.AreEqual(data[b], reply[b], "First reply byte value = " + data[b]);
                        b = data.Length/2;
                        Assert.AreEqual(data[b], reply[b], "Middle reply byte " + b + "value = " + data[b]);
                        b++;
                        Assert.AreEqual(data[b], reply[b], "Middle reply byte " + b + "value = " + data[b]);
                        b = data.Length - 1;
                        Assert.AreEqual(data[b], reply[b], "Last reply byte value = " + data[b]);
                    }
                }
            }
        }

        [TestMethod, TestCategory("Failures"), TestCategory("Messaging"), TestCategory("Stress"), TestCategory("Azure"), TestCategory("Table")]
        public async Task STMessagingTest_LargeMsgBlob_WriteToAzureTable()
        {
            const string testName = "LargeMsgBlob_WriteToAzureTable";
            const int numGrains = 3;
            TimeSpan testDuration = TimeSpan.FromMinutes(20);

            Stopwatch clock = new Stopwatch();
            clock.Start();

            while (clock.Elapsed < testDuration)
            {
                for (int iteration = 1; iteration <= 32; iteration++)
                {
                    int dataSize = iteration * 1000;

                    Console.WriteLine("{0}::Iteration #{1} with {2} grains and data size = {3}", testName, iteration, numGrains, dataSize);

                    byte[] data = new byte[dataSize];
                    for (int i = 0; i < dataSize; i++)
                    {
                        data[i] = (byte) (i & 0xFF);
                    }

                    IAzureTableStorageAccessSMGrain[] grains = new IAzureTableStorageAccessSMGrain[numGrains];
                    for (int i = 0; i < numGrains; i++)
                    {
                        long key = i + 1;
                        IAzureTableStorageAccessSMGrain grain = AzureTableStorageAccessSMGrainFactory.GetGrain(key);
                        Assert.AreEqual(key, grain.GetPrimaryKeyLong());
                        Assert.AreEqual(key.ToString(CultureInfo.InvariantCulture), await grain.GetLabel());
                        grains[i] = grain;
                    }

                    for (int i = 0; i < grains.Length; i++)
                    {
                        string partitionKey = testName;
                        string rowKey = (i + 1).ToString(CultureInfo.InvariantCulture);
                        Console.WriteLine("{0} - Writing {1} byte message to partiionKey={2} rowKey={3}", testName, data.Length, partitionKey, rowKey);
                        var grain = grains[i];
                        await grain.WriteToAzureTable(partitionKey, rowKey, data);
                    }
                }
            }
        }

        [TestMethod, TestCategory("Failures"), TestCategory("Messaging"), TestCategory("Stress"), TestCategory("Azure"), TestCategory("Blob")]
        public async Task STMessagingTest_LargeMsgBlob_WriteToAzureBlob()
        {
            const string testName = "LargeMsgBlob_WriteToAzureBlob";
            const int numGrains = 3;
            TimeSpan testDuration = TimeSpan.FromMinutes(20);

            Stopwatch clock = new Stopwatch();
            clock.Start();

            while (clock.Elapsed < testDuration)
            {
                for (int iteration = 1; iteration <= 20; iteration++)
                {
                    int dataSize = iteration * 100 * 1000;

                    Console.WriteLine("{0}::Iteration #{1} with {2} grains and data size = {3}", testName, iteration, numGrains, dataSize);

                    byte[] data = new byte[dataSize];
                    for (int i = 0; i < dataSize; i++)
                    {
                        data[i] = (byte)(i & 0xFF);
                    }

                    IAzureBlobStorageAccessSMGrain[] grains = new IAzureBlobStorageAccessSMGrain[numGrains];
                    for (int i = 0; i < numGrains; i++)
                    {
                        long key = i + 1;
                        IAzureBlobStorageAccessSMGrain grain = AzureBlobStorageAccessSMGrainFactory.GetGrain(key);
                        Assert.AreEqual(key, grain.GetPrimaryKeyLong());
                        Assert.AreEqual(key.ToString(CultureInfo.InvariantCulture), await grain.GetLabel());
                        grains[i] = grain;
                    }

                    for (int i = 0; i < grains.Length; i++)
                    {
                        string blobName = (i + 1).ToString(CultureInfo.InvariantCulture);
                        string containerName = testName;
                        Console.WriteLine("{0} - Writing {1} byte message to ContainerName={2} BlobName={3}", testName, data.Length, containerName, blobName);
                        var grain = grains[i];
                        grain.WriteToAzureBlob(containerName, blobName, data).Wait();
                    }
                }
            }
        }
    }
}
// ReSharper restore ConvertToConstant.Local
