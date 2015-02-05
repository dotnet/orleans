using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;

// This is a test program, to understand and evaluate the error handling behaoivour of Tasks. It is not part of Orleans runtime.

// if tasks are created with DetachedFromParent, 
    //if joining the task with ContinueWith
        // if expecting the exception
            //the thrown exception is expected and not propagated any further
        // if NOT expecting the exception
            //the thrown exception is propagated 
    //if NOT joining the task with ContinueWith
        //

// if tasks are created without DetachedFromParent, 
    //if joining the task with ContinueWith
        //the exception thrown at one task propogates to ALL the upper tasks, even if expected in the middle


namespace ErrorHandlingTest
{
    class TaskTest
    {
        // understand inner exceptions
        // how to fail outer operation when inner opeartion failed (outer calling inner and waiting with continueWith)
        private static readonly int maxLevel = 5;
        private static Semaphore _sem = new Semaphore(0,1);

       // public static void Print(int n, string s)
       // {
        //    Print(n, s, null);
       // }

      
        public static void Print(int n, string s)
        {
            for (int i = maxLevel; i >= n; i--)
            {
                Console.Write("   ");
            }
            //if(t==null){
             //    Console.WriteLine("[" + n + "]: " + s);
            //}
            //else
            {
                Console.WriteLine("[" + n + ": Curr." + OrleansTaskExtentions.ToString(Task.Current) + "] " + s);
            }
        }
 
        public static Task<int> doOperation2(int level)
        {
            Task<int> task = Task.Factory.StartNew<int>(recLevelObj =>
            {
                int recLevel = (int)recLevelObj;
                Print(recLevel, "started");
                if (recLevel > 0)
                {
                    Thread.Sleep(1000);
                    Task<int> res2 = doOperation2((recLevel) - 1);

                   // if ((recLevel % 2) == 0)
                    {
                        Print(recLevel, "Calling ContinueWith.");
                        res2.ContinueWith((Task<int> z) =>
                        {
                            if (z.Status == TaskStatus.RanToCompletion)
                            {
                                Print(recLevel, "ContinueWith succeeded with: " + z.Result);
                            }
                            else if (z.Status == TaskStatus.Faulted)
                            {
                                //Print(recLevel, "ContinueWith failed with: ");
                                //if ((recLevel % 2) == 0)
                                {
                                    Print(recLevel, "ContinueWith failed with: " + z.Exception.InnerException.ToString());
                                }
                            }
                            else
                            {
                                Print(recLevel, "ContinueWith is: " + z.Status);
                            }
                            Print(recLevel, "ContinueWith END");
                            return 66;
                        //}, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.DetachedFromParent);
                        //}, TaskContinuationOptions.DetachedFromParent);
                       });;
                    }
                    Print(recLevel, "Z");
                }
                else{
                    Thread.Sleep(1000);
                    Print(recLevel, "Throwing.");
                    //_sem.Release();
                    //Print(recLevel, "Finished Throwing.");
                    throw new ActorRuntimeException("GABI intentional failure.");
                }
                Print(recLevel, "finished");
                return 77;
            //}, level, TaskCreationOptions.DetachedFromParent);
            }, level);
            return task;
        }


        static void Main1(string[] args)
        {
            Console.WriteLine("Hello Gabi.");
            Task<int> res = doOperation2(maxLevel);

            res.ContinueWith((Task<int> z) =>
            {
                Console.WriteLine("APP ContinueWith: ");
                //_sem.WaitOne();
                if (res.Status == TaskStatus.RanToCompletion)
                {
                    Console.WriteLine("APP succeeded with: " + res.Result);
                }
                else if (res.Status == TaskStatus.Faulted)
                {
                    Console.WriteLine("APP failed with:" + res.Exception.InnerException.ToString());
                }
                else
                {
                    Console.WriteLine("APP is: " + res.Status);
                }
                Console.WriteLine("APP End ContinueWith");
            });
            Console.WriteLine("APP Y");

            Console.ReadLine();
        }
    }
}

#if false
                        if (z.Status == TaskStatus.RanToCompletion)
                        {
                            TaskTest.Print(recLevel, "ContinueWith succeeded with: " + z.Result + " on Task: " + z.ToString());
                        }
                        else if (z.Status == TaskStatus.Faulted)
                        {
                            //Print(recLevel, "ContinueWith failed with: ");
                            //if ((recLevel % 2) == 0)
                            {
                                String s = z.Exception.GetBaseException().ToString();
                                TaskTest.Print(recLevel, "### ContinueWith failed with EXCEPTION: " );//+ z.Exception.GetBaseException().Message.ToString() + " on Task: " + z.ToString());
                            }
                        }
                        else
                        {
                            TaskTest.Print(recLevel, "ContinueWith is: " + z.Status + " on Task: " + z.ToString());
                        }
#endif

/*   public static AsyncValue<int> doOperation1(int x)
   {
       Console.WriteLine("doOperation1 started");
       Task<int> res = Task.Factory.StartNew<int>( y =>
       {
           Thread.Sleep(1000);
           throw new ActorRuntimeException("intentional failure.");
           //return ((int)y)+1;
       }, x);
       Console.WriteLine("doOperation1 finished");
       return res;
   }*/

/*   Task t1 = new Task((object x) =>
   {
       Console.WriteLine(x);
   }, "T1");

   Action<object> d2 = delegate(object obj)
   {
       Console.WriteLine(obj);
   };
   Task t2 = new Task(d2, "T2");
   Task t3 = new Task(d2, "T3");

   //Action<string> d3 = new Action<string> ((string x) =>
   //{
   //    Console.WriteLine(x);
   //});
   // generics are not covariant. 
   //Task t3 = new Task(d3, "T2");
          
   //List<Task> myAL = new List<Task>();
   //myAL.Add(t1);

   //for (int i = 0; i < 10; i++)
   {
       //myAL.Add(new Task(d2, "T" + i));
       //myAL[i].Start();
   }*/