using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;

namespace ErrorHandlingTest
{
    class TaskTestClean
    {
        private static readonly int maxLevel = 3;

        private static void Print(int n, string s)
        {
            for (int i = maxLevel; i >= n; i--)
            {
                Console.Write("   ");
            }
            Console.WriteLine("[" + n + "]: " + s);
        }

        public static Task<int> doOperation2(int level)
        {
            Task<int> task = Task.Factory.StartNew<int>(recLevelObj =>
            {
                int recLevel = (int)recLevelObj;
                Print(recLevel, "started");
                Thread.Sleep(1000);
                if (recLevel > 0)
                {
                    Task<int> task1 = doOperation2(recLevel - 1);
                    task1.ContinueWith((Task<int> z) =>
                    {
                        if (z.Status == TaskStatus.RanToCompletion)
                        {
                            Print(recLevel, "ContinueWith succeeded with: " + z.Result);
                        }
                        else if (z.Status == TaskStatus.Faulted)
                        {
                            Print(recLevel, "ContinueWith failed with: " + z.Exception.GetBaseException().ToString());
                        }
                        else
                        {
                            Print(recLevel, "ContinueWith is: " + z.Status);
                        }
                        Print(recLevel, "ContinueWith END");
                    //});
                    });//, TaskContinuationOptions.ExecuteSynchronously);

                    task1.ContinueWith((Task<int> z) =>
                    {
                        {
                            Print(recLevel, "SECOND ContinueWith is: " + z.Status);
                        }
                        Print(recLevel, "ContinueWith END");
                   });
                }
                else
                {
                    Print(recLevel, "Last call finished.");
                    throw new ActorRuntimeException("GABI intentional failure.");
                }
                Print(recLevel, "finished");
                return 77;
            //}, level, TaskCreationOptions.DetachedFromParent);
            }, level); //, TaskCreationOptions.DetachedFromParent);
            return task;
        }


        static void Main2(string[] args)
        {
            Console.WriteLine("Hello TaskTestClean.");
            Task<int> res = doOperation2(maxLevel);

            res.ContinueWith((Task<int> z) =>
            {
                Console.WriteLine("\nAPP ContinueWith: ");
            });
            Console.WriteLine("APP before ReadLine()");
            Console.ReadLine();
        }


        public static void LibraryCode()
        {
            Task.Factory.StartNew(() =>
            {
                Console.WriteLine("Inside Task.");
                throw new Exception("GABI intentional failure.");
               // do some work that may throw an exception
            }).ContinueWith(t =>
            {
                Console.WriteLine("\n" +  t.Exception.ToString());
            }, TaskContinuationOptions.OnlyOnFaulted);
        }


        static void Main3(string[] args)
        {
            Console.WriteLine("Hello TaskTestClean.");
            LibraryCode();
            Console.ReadLine();
        }

    }
}