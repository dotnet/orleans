using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.RuntimeCore;


namespace UnitTests
{
    [TestClass]
    public class OrleansTaskTest : UnitTestBase
    {
        private static readonly int m_maxLevel = 2;
        private static int m_scenario = 1;
        private static Boolean[] DO_THROW;
        private static Boolean[] DO_CONTINUE; 
        private static Boolean[] DO_CONTINUE_EXCEPTION;
        private static Boolean[] DO_ASK_RESULT;
        private static Semaphore m_sem = new Semaphore(0, 1);

        private static void Print(int n, string s)
        {
            for (int i = m_maxLevel; i >= n; i--)
            {
                Debug.Write("   ");
            }
            Debug.WriteLine("[" + n + ": Curr." + Task.CurrentId + "] " + s);
        }

        private static AsyncValue<int> continuationAction(int recLevel, AsyncValue<double> task)// , Boolean useExceptionHandler)
        { 
            Func<double, int> contFunc = (double z) =>
            {
                Print(recLevel, "SUCCESSFULL ContinueWith on Task: " + z.ToString());
                return 1;
            };

            Action<Exception> excAction = (Exception exc) =>
            {
                Print(recLevel, "EXCEPTION ContinueWith: Intermediate catcher: failed with:" + exc.GetBaseException().Message.ToString());
            };

            Print(recLevel, "Calling ContinueWith on Task: " + task.ToString());
            AsyncValue<int> contTask = null;
            //if (useExceptionHandler)
            //{
            //    contTask = task.ContinueWithFunction<int>(contFunc, excAction);
            //}
            //else
            {
                contTask = task.ContinueWith<int>(contFunc);
            }
            return contTask;
        }

        public static AsyncValue<double> doOperation(int level)
        {
            AsyncValue<double> task = AsyncValue<double>.StartNew(() =>
            {
                int recLevel = level;// (int)recLevelObj;
                Print(recLevel, "started");
                if (recLevel == 0)
                {
                    Thread.Sleep(100);
                    if (DO_THROW[recLevel])
                    {
                        Print(recLevel, "Throwing.");
                        throw new ActorRuntimeException("GABI intentional failure.");
                    }
                    return 1;
                }
                else if (recLevel > 0)
                {
                    Thread.Sleep(100);
                    AsyncValue<double> res2 = doOperation(recLevel - 1);

                    if (DO_CONTINUE[recLevel])
                    {
                        //AsyncValue<int> contTask = continuationAction(recLevel, res2); //, DO_CONTINUE_EXCEPTION[recLevel]);
                        AsyncValue<int> contTask = res2.ContinueWith<int>((double z) =>
                                {
                                    Print(recLevel, "SUCCESSFULLY ContinuedWith on Task: " + res2.ToString());
                                    return 1;
                                });
                        try
                        {
                            contTask.Wait();
                        }
                        catch (Exception exc)
                        {
                            Print(recLevel, "FAULTY ContinuedWith : Caught an exception on contTask.Wait() on task " + (res2).ToString() + " EXC=" + exc.GetBaseException().Message.ToString());
                        }
                    }
                    else
                    {
                        Assert.IsTrue(DO_CONTINUE_EXCEPTION[recLevel] == false); //can't give exception handler without continuation handler
                        //...
                    }

                    Print(recLevel, "finished");
                    if (DO_ASK_RESULT[recLevel])
                    {
                        double d = 0;
                        Print(recLevel, "Asking for previous task returned ");
                        try
                        {
                            d = res2.GetValue();
                        }
                        catch (Exception exc)
                        {
                            Print(recLevel, "Caught an exception on task.Result on task " + res2.ToString() + " EXC=" + exc.GetBaseException().Message.ToString());
                        }
                        Print(recLevel, "Previous task returned " + d);
                        return d + 1;
                    }
                    else
                    {
                        return 17.1;
                    }
                }
                Assert.Fail("Unreachable code.");
                return -1;
            });
            return task;
        }

        static void RunTest()
        {
            Debug.WriteLine("-------------------------------------------------------");
            Debug.WriteLine("Hello OrleansTaskTest. Running scenario " + m_scenario + ":");

            AsyncValue<double> res = doOperation(m_maxLevel);
            res.Wait();
            AsyncValue<int> contAction = res.ContinueWith<int>(
                (double z) =>
                {
                    Debug.WriteLine("\nMain ContinueWith: Task finished with Result " + res.GetValue() + " with status ");// + res.Status);
                    //Assert.IsTrue(m_scenario == 1 || m_scenario == 3 || m_scenario == 5 || m_scenario == 6 || m_scenario == 7);
                    //Assert.IsTrue(res.Status == TaskStatus.RanToCompletion && res.Status != TaskStatus.Faulted);
                    m_sem.Release();
                    return - 2;
                }//,
                //(Exception data) => 
                //{
                //    Console.WriteLine("\nGLOBAL APP catcher: Task finished with status " + res.Status);
                //    Console.WriteLine("GLOBAL APP catcherh: failed with:" + data.GetBaseException().ToString());
                //    Assert.IsTrue(m_scenario == 2 || m_scenario == 4);
                //    Assert.IsTrue(res.Status == TaskStatus.Faulted);
                //    m_sem.Release();
                //}
            );
            contAction.Wait();

            Debug.WriteLine("RunTest() function finished");
            //Console.ReadLine();
            m_sem.WaitOne();
            Debug.WriteLine("Test scenario " + m_scenario + " passed successfully.\n\n");
        }

        static void Scenario1()
        {
            // no exception - successfull flow
            m_scenario = 1;
            OrleansTaskTest.DO_THROW = new Boolean[] { false, false, false };
            OrleansTaskTest.DO_CONTINUE = new Boolean[] { false, false, false };
            OrleansTaskTest.DO_CONTINUE_EXCEPTION = new Boolean[] { false, false, false };
            OrleansTaskTest.DO_ASK_RESULT = new Boolean[] { false, false, false };
            RunTest();
        }
        static void Scenario2()
        {
            // do exception - do not continue, do not catch. Should propage exception to main.
            m_scenario = 2;
            OrleansTaskTest.DO_THROW = new Boolean[] { true, false, false };
            OrleansTaskTest.DO_CONTINUE = new Boolean[] { false, false, false };
            OrleansTaskTest.DO_CONTINUE_EXCEPTION = new Boolean[] { false, false, false };
            OrleansTaskTest.DO_ASK_RESULT = new Boolean[] { false, false, false };
            RunTest();
        }
        static void Scenario3()
        {
            // do exception - continue, catch. Should not propage exception to main.
            m_scenario = 3;
            OrleansTaskTest.DO_THROW = new Boolean[] { true, false, false };
            OrleansTaskTest.DO_CONTINUE = new Boolean[] { false, true, true };
            OrleansTaskTest.DO_CONTINUE_EXCEPTION = new Boolean[] { false, true, false };
            OrleansTaskTest.DO_ASK_RESULT = new Boolean[] { false, false, false };
            RunTest();
        }
        static void Scenario4()
        {
            // do exception - continue, do not catch. Should propage exception to main.
            m_scenario = 4;
            OrleansTaskTest.DO_THROW = new Boolean[] { true, false, false };
            OrleansTaskTest.DO_CONTINUE = new Boolean[] { false, true, false };
            OrleansTaskTest.DO_CONTINUE_EXCEPTION = new Boolean[] { false, false, false };
            OrleansTaskTest.DO_ASK_RESULT = new Boolean[] { false, false, false };
            RunTest();
        }

        static void Scenario5()
        {
            // do exception - continue, catch at the second catcher. Should not propage exception to main.
            m_scenario = 5;
            OrleansTaskTest.DO_THROW = new Boolean[] { true, false, false };
            OrleansTaskTest.DO_CONTINUE = new Boolean[] { false, true, true };
            OrleansTaskTest.DO_CONTINUE_EXCEPTION = new Boolean[] { false, false, true };
            OrleansTaskTest.DO_ASK_RESULT = new Boolean[] { false, false, false };
            RunTest();
        }

        static void Scenario6()
        {
            // do exception - continue, catch at the second catcher. Should not propage exception to main.
            m_scenario = 6;
            OrleansTaskTest.DO_THROW = new Boolean[] { true, false, false };
            OrleansTaskTest.DO_CONTINUE = new Boolean[] { false, true, true };
            OrleansTaskTest.DO_CONTINUE_EXCEPTION = new Boolean[] { false, true, false };
            OrleansTaskTest.DO_ASK_RESULT = new Boolean[] { false, false, false };
            RunTest();
        }

        static void Scenario7()
        {
            // do exception - ask result
            m_scenario = 7;
            OrleansTaskTest.DO_THROW = new Boolean[] { true, false, false };
            OrleansTaskTest.DO_CONTINUE = new Boolean[] { false, true, true };
            OrleansTaskTest.DO_CONTINUE_EXCEPTION = new Boolean[] { false, true, true };
            OrleansTaskTest.DO_ASK_RESULT = new Boolean[] { true, true, true };
            RunTest();
            
        }

        //[TestMethod]
        public void OrleansTaskTestBasic()
        {
            OrleansTaskTest.DO_THROW = new Boolean[] { false, false, false };
            OrleansTaskTest.DO_CONTINUE = new Boolean[] { false, false, false };
            OrleansTaskTest.DO_CONTINUE_EXCEPTION = new Boolean[] { false, false, false };
            OrleansTaskTest.DO_ASK_RESULT = new Boolean[] { false, false, false };
            //OrleansTaskTest.DO_ASK_RESULT = new Boolean[] { true, true, true };
            Scenario1();
            Scenario2();
            //  Scenario3();
            //  Scenario4();
            //  Scenario5();
            //  Scenario6();
            Scenario7();
        }

        //static void Main(string[] args){}
    }
}


