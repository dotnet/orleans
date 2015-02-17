#if DEBUG

using System;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Orleans;
using Orleans.Runtime;
using UnitTests;
using UnitTests.AsyncPrimitivesTests;
using UnitTests.General;
using UnitTests.StreamingTests;
using UnitTests.StressTests;
using ProxyErrorGrain;
using UnitTests.TimerTests;

#endif

namespace Test
{
    public class Program
    {
        public static void Main(string[] args)
        {
#if DEBUG
            try
            {
                ////RequestContextSiloTests tests = new RequestContextSiloTests();
                ////tests.RequestContext_Test1();
                //GenericGrainTests tests = new GenericGrainTests();
                //tests.Generic_SelfManagedGrainControlFlow();

                //DisposeTest.Test.Run();
                //AsyncLockTests.Run();

                TestContext testContext = new MockTestContext();

                Console.WriteLine("**** Start of Test");

                //StreamLimitTests.ClassInitialize(testContext);
                var x = new StreamLimitTests();
                x.TestContext = testContext;
                x.TestInitialize();

                Console.WriteLine("**** Initialization complete - ready to start test run.");
                Console.WriteLine("--> Take memory snapshot #1, then Press a key to start.");
                Console.ReadLine();

                Console.WriteLine("\n\n===== Test()\n");
                x.SMS_Churn_FewPublishers_C9_ManyStreams().Wait();
                Console.WriteLine("\n\n===== Cleanup\n");
                x.TestCleanup();
                Console.WriteLine("**** Ended Test successfully");
            }
            catch (Exception exc)
            {
                //Debugger.Break();
                Console.WriteLine("\n\n===== Exception during test execution\n" + exc);
            }
            finally
            {
                Console.WriteLine();
                Console.WriteLine("--> Take memory snapshot #2, then Press a key to stop.");
                Console.ReadLine();
                StreamLimitTests.ClassCleanup();
            }
#endif

            //Client.Initialize();

            //const int n = 20;
            //const int report = 500;
            //var watch = new Stopwatch();
            //watch.Start();
            //var place = Enumerable.Range(0, n).Select(i => new[] { GrainStrategy.PartitionPlacement(i) }).ToList();
            //var direct = place.Select((s, i) => ProxyErrorGrainFactory.CreateGrain(A: i, Strategies: s)).ToList();
            //var pipeline = new Pipeline(report);
            //for (int i = 0; i < report * 10; i++)
            //{
            //    pipeline.Add(direct[i % direct.Count].CreateProxy());
            //    if (i > 0 && (i%report) == 0)
            //    {
            //        watch.Stop();
            //        Console.WriteLine("{0} iterations = {1}/s", i, report*1000/watch.ElapsedMilliseconds);
            //        watch.Reset();
            //        watch.Start();
            //    }
            //}

            //if(!runBenchmark)
            //{
            //    Console.WriteLine("**** Ended Test successfully!");
            //    Console.ReadLine();
            //}

#if false
            int i;
            try
            {
                GrainActivateDeactivateTests.ClassInitialize(null);
                var tests = new GrainActivateDeactivateTests();
                for (i = 0; i < 100; ++i)
                {
                    Console.WriteLine("Test #{0}", i);
                    Debug.WriteLine("Test #{0}", i);
                    try
                    {
                        tests.TestInitialize();
                        tests.TaskAction_Deactivate();
                    }
                    finally
                    {
                        tests.TestCleanup();
                    }
                }
            }
            catch (Exception exc)
            {
                Debugger.Break();
                Console.WriteLine("\n\n===== Exception during test execution\n");
                Console.WriteLine(exc.ToString());
            }
            finally
            {
                GrainActivateDeactivateTests.ClassCleanup();
            }
#endif
        }
    }
}

