using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;

namespace UnitTests.AsyncPrimitivesTests
{
    // currently, this isn't a unit test. this is here for demonstration purposes only. in the future, however,
    // we hope to turn this into a unit test suite for AsyncLock.
    public class AsyncLockTests
    {
        private AsyncLock _initLock = new AsyncLock();
        private bool insideLock = false;

        private async Task<int> WorkUnderLock(int worker)
        {
            Console.WriteLine("Starting WorkUnderLock " + worker);
            using (await _initLock.LockAsync())
            {
                return await DoActualWork(worker);
            }
        }

        private async Task<int> WorkWithoutLock(int worker)
        {
            Console.WriteLine("Starting WorkWithoutLock " + worker);
            return await DoActualWork(worker);
        }

        private async Task<int> DoActualWork(int worker)
        {
            Console.WriteLine("Starting actual work" + worker);
            Assert.IsFalse(insideLock);
            insideLock = true;
            //Thread.Sleep(100);
            await Task.Delay(50);
            Assert.IsTrue(insideLock);
            await Task.Delay(50);
            Assert.IsTrue(insideLock);
            insideLock = false;
            Console.WriteLine("Ending actual work" + worker);
            return 5;
        }

        internal Task MultipleConcurrentWorkers(bool underLock)
        {
            List<Task> workers = new List<Task>();
            for (int i = 0; i < 10; i++)
            {
                int capture = i;
                Task task = null;
                if (underLock)
                {
                    task = new Task(() => WorkUnderLock(capture).Wait());
                }
                else
                {
                    task = new Task(() => WorkWithoutLock(capture).Wait());
                }
                workers.Add(task);
                task.Start();
            }
            Console.WriteLine("Done creating.");
            return Task.WhenAll(workers);
        }

        public static void CorrectRun()
        {
            AsyncLockTests demo = new AsyncLockTests();
            demo.MultipleConcurrentWorkers(true).Wait();
            demo.MultipleConcurrentWorkers(true).Wait();
        }

        public static void FailureRun()
        {
            AsyncLockTests demo = new AsyncLockTests();
            demo.MultipleConcurrentWorkers(false).Wait();
        }

        public static void Run()
        {
            CorrectRun();
            Console.WriteLine("=========================");
            //FailureRun();
        }
    }
}
